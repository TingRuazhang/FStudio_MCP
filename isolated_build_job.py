"""Durable standalone job runner for the private-desktop native compiler."""
import argparse
import json
import os
from pathlib import Path
import time
import traceback
from datetime import datetime, timezone

from build_snapshot import BuildSnapshot, prune_recent_projects
from isolated_compiler import IsolatedCompiler


def publish_status(directory,state):
    """Write the job state atomically, tolerating the Windows sharing violation on the rename.

    The poller opens `status.json` while this worker renames `status.tmp` onto it, and on Windows a rename onto a
    file another handle holds open fails with ERROR_ACCESS_DENIED. Losing a publish would lose the build itself:
    measured 2026-09-08 on build f5b34fd69ee04fc7a62bac651c4dc0ee, where one such collision turned a normal compile
    into a `failed` job with `compiler_success=null` and no native diagnostics at all.
    """
    temporary=directory/'status.tmp'
    temporary.write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf8')
    deadline=time.monotonic()+5
    while True:
        try:
            temporary.replace(directory/'status.json')
            return
        except PermissionError:
            if time.monotonic()>=deadline:raise
            time.sleep(.05)


def run_job(request_file):
    request_file=Path(request_file).resolve(strict=True)
    directory=request_file.parent
    request=json.loads(request_file.read_text(encoding='utf8'))
    state=dict(build_id=directory.name,status='preparing',compiler_success=None,
               runner_pid=os.getpid(),project=request['project'],cleanup_complete=False)
    def persist(**changes):
        state.update(changes)
        state['updated_utc']=datetime.now(timezone.utc).isoformat()
        publish_status(directory,state)
    snapshot=None
    compiler=None
    terminal='failed'
    try:
        persist()
        if (directory/'cancel').exists():
            terminal='cancelled'
            return
        snapshot=BuildSnapshot(request['project'],workspace=request['workspace'],artifact_dir=directory)
        compiler=IsolatedCompiler(snapshot,request['addin'],request['bin'])
        persist(status='starting',worker_pid=compiler.process.pid,desktop=compiler.process.name)
        while True:
            if (directory/'cancel').exists():compiler.request_cancel()
            result=compiler.tick()
            if result is not None:break
            # State observation does not impose a native execution timeout.
            # A queued native operation is retained and polled by the session.
            persist(status=compiler.phase,native_build_id=compiler.build_id,
                    cancel_requested=compiler.cancel_requested,
                    observation_error=compiler.observation_error)
            time.sleep(.25)
        terminal=result['status']
        persist(status='archiving',native_build_id=compiler.build_id,
                compiler_success=result.get('compiler_success'),native_result=result)
        compiler.close()
        compiler=None
        outputs=snapshot.archive_outputs()
        persist(outputs=outputs,source_unchanged=snapshot.source_unchanged())
    except BaseException:
        terminal='failed'
        persist(exception=traceback.format_exc())
    finally:
        cleanup_errors=[]
        if compiler is not None:
            try:compiler.close()
            except Exception:cleanup_errors.append(traceback.format_exc())
        if snapshot is not None and not cleanup_errors:
            try:snapshot.cleanup()
            except Exception:cleanup_errors.append(traceback.format_exc())
        persist(status=terminal if not cleanup_errors else 'cleanup_failed',
                cleanup_complete=not cleanup_errors,cleanup_errors=cleanup_errors)


prune_recent_projects()

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('request')
    run_job(parser.parse_args().request)
