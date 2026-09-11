"""Native compiler session owned by one background job."""
import json
from pathlib import Path
import shutil
import time

from desktop_process import DesktopProcess
from visible_host import VisibleHost


class IsolatedCompiler:
    def __init__(self,snapshot,addin_source,bin_dir):
        self.snapshot=snapshot
        self.process=None
        self.pending={}
        self.build_id=None
        self.cancel_requested=False
        self.cancel_sent=False
        self.cancel_error=None
        self.observation_error=None
        self.phase='starting'
        addin=snapshot.artifact_dir/'addin'
        addin.mkdir()
        for name in ('FStudioMcp.Visible.dll','FStudioMcp.addin'):
            shutil.copy2(Path(addin_source)/name,addin/name)
        state=snapshot.artifact_dir/'host'
        (addin/'host-settings.json').write_text(json.dumps(dict(workspace=str(snapshot.owner),state_directory=str(state))),encoding='utf8')
        self.host=VisibleHost(state/'endpoint.json')
        self.process=DesktopProcess(Path(bin_dir)/'FStudio.exe',
            ['-addindir:'+str(addin),str(snapshot.project)],cwd=bin_dir)
        snapshot.write_record('worker.json',dict(pid=self.process.pid,desktop=self.process.name))

    def operation(self,key,op,**args):
        # A pending native job is always polled by its original id. Never
        # resubmit a mutation because the observation period expired.
        if key not in self.pending:
            self.pending[key]=self.host.request(op,**args)
        job=self.pending[key]
        if 'job_id' not in job:return job
        try:
            value=self.host.request('job',job_id=job['job_id'])
            self.observation_error=None
        except RuntimeError as error:
            if not any(message in str(error) for message in (
                    'Visible host pipe is unavailable',
                    'Host response timed out')):raise
            # This is a read of an already acknowledged operation. Preserve
            # its identity and observe again; never resubmit the operation.
            self.observation_error=str(error)
            return None
        if value['status']=='failed':raise RuntimeError(value['error'])
        if value['status']=='completed':return value['result']
        return None

    def request_cancel(self):self.cancel_requested=True

    def tick(self):
        code=self.process.poll()
        if code is not None:raise RuntimeError('Native compiler process exited: '+str(code))
        if self.cancel_requested and self.build_id is None:
            self.close()
            return dict(status='cancelled',compiler_success=None)
        if self.phase=='starting':
            if not self.host.endpoint.is_file():return None
            health=self.operation('health','health')
            if health is None:return None
            if health['pid']!=self.process.pid:raise RuntimeError('Worker endpoint does not match owned process')
            self.phase='opening'
        if self.phase=='opening':
            opened=self.operation('open','open_project',file=str(self.snapshot.project))
            if opened is None:return None
            self.phase='compiling'
        if self.build_id is None:
            started=self.operation('start','build_start')
            if started is None:return None
            self.build_id=started['build_id']
        # Read status with the same native host job until it completes, then
        # request a fresh snapshot on the next tick.
        result=self.operation('status','build_status',build_id=self.build_id)
        if result is None:return None
        self.pending.pop('status',None)
        if result['status'] not in ('completed','failed','cancelled','ui_required'):
            if self.cancel_error is not None:raise RuntimeError(self.cancel_error)
            if self.cancel_requested and not self.cancel_sent:
                try:
                    cancelled=self.operation('cancel','build_cancel',build_id=self.build_id)
                    if cancelled is None:return None
                    self.cancel_sent=True
                except RuntimeError as error:
                    # Native completion can win the race after the status read.
                    # Observe it once before classifying a rejected cancel as failure.
                    self.cancel_error=str(error)
            return None
        if self.cancel_error is not None:result['cancel_observation_error']=self.cancel_error
        self.snapshot.write_record('native-result.json',result)
        self.phase='finished'
        return result

    def close(self):
        if self.process is not None:
            self.process.close()
            self.process=None

    def __enter__(self):return self
    def __exit__(self,*unused):self.close()
