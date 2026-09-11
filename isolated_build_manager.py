"""Persistent build-job client; runner lifetime is independent of MCP stdin."""
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid
import ctypes
import hashlib
from contextlib import contextmanager


@contextmanager
def launch_lock(project):
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateMutexW.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_wchar_p]
    kernel.CreateMutexW.restype=ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32]
    kernel.WaitForSingleObject.restype=ctypes.c_uint32
    kernel.ReleaseMutex.argtypes=[ctypes.c_void_p]
    kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    name='Local\\FStudioMcpLaunch_'+hashlib.sha256(str(project).casefold().encode('utf8')).hexdigest()
    handle=kernel.CreateMutexW(None,False,name)
    if not handle:raise ctypes.WinError(ctypes.get_last_error())
    acquired=False
    try:
        result=kernel.WaitForSingleObject(handle,5000)
        if result not in (0,128):raise RuntimeError('Another client is preparing this project build; retry status first')
        acquired=True
        yield
    finally:
        if acquired:kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)

TERMINAL={'completed','failed','cancelled','ui_required','cleanup_failed'}


class IsolatedBuildManager:
    def __init__(self,root,workspace,bin_dir):
        self.root=Path(root).resolve()
        self.workspace=Path(workspace).resolve()
        self.bin=Path(bin_dir).resolve()
        self.jobs=self.root/'artifacts/isolated-jobs'
        self.jobs.mkdir(parents=True,exist_ok=True)

    def directory(self,build_id):
        if not isinstance(build_id,str) or not re.fullmatch('[0-9a-f]{32}',build_id):
            raise ValueError('Invalid build_id')
        path=self.jobs/build_id
        if not path.is_dir() or path.is_symlink():raise ValueError('Unknown build_id')
        return path

    def status(self,build_id):
        directory=self.directory(build_id)
        request=json.loads((directory/'request.json').read_text(encoding='utf8'))
        project=Path(request['project']).resolve()
        if not project.is_relative_to(self.workspace):raise ValueError('Build belongs to another workspace')
        state=directory/'status.json'
        queued=dict(build_id=build_id,status='queued',project=str(project),compiler_success=None,cleanup_complete=False)
        # The worker republishes status.json with an atomic rename; on Windows a read that lands during the rename
        # fails with ERROR_ACCESS_DENIED, so a momentary failure is retried and then reported as still non-terminal
        # rather than either crashing the poll or inventing an outcome the job never wrote.
        deadline=time.monotonic()+2
        while True:
            try:
                return json.loads(state.read_text(encoding='utf8'))
            except FileNotFoundError:
                return queued
            except OSError as error:
                if time.monotonic()>=deadline: return dict(queued,status_read_error=str(error))
                time.sleep(.05)

    def cancel(self,build_id):
        state=self.status(build_id)
        if state['status'] in TERMINAL:return state
        (self.directory(build_id)/'cancel').touch(exist_ok=True)
        return dict(state,cancel_requested=True)

    def start(self,project):
        project=Path(project).resolve(strict=True)
        if project.suffix.lower()!='.fsprj' or not project.is_relative_to(self.workspace):
            raise ValueError('Expected workspace project')
        with launch_lock(project):return self._start_locked(project)

    def _start_locked(self,project):
        # Do not infer that an unresponsive runner has stopped. Outstanding jobs
        # require observation/recovery before another compiler may be launched.
        for directory in self.jobs.iterdir():
            if not directory.is_dir() or not re.fullmatch('[0-9a-f]{32}',directory.name):continue
            request_file=directory/'request.json'
            if not request_file.exists():continue
            request=json.loads(request_file.read_text(encoding='utf8'))
            if Path(request['project']).resolve()!=project:continue
            previous=self.status(directory.name)
            if previous['status'] not in TERMINAL or not previous.get('cleanup_complete',False):
                raise RuntimeError('Existing build requires observation: '+directory.name)
        build_id=uuid.uuid4().hex
        directory=self.jobs/build_id
        directory.mkdir()
        request=dict(project=str(project),workspace=str(self.workspace),addin=str(self.root/'artifacts/visible-addin'),bin=str(self.bin))
        (directory/'request.json').write_text(json.dumps(request),encoding='utf8')
        try:
            with (directory/'runner.log').open('wb') as log:
                runner=subprocess.Popen([sys.executable,'-X','utf8',str(self.root/'isolated_build_job.py'),str(directory/'request.json')],
                    cwd=self.root,stdin=subprocess.DEVNULL,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
            (directory/'runner.json').write_text(json.dumps(dict(pid=runner.pid)),encoding='utf8')
        except Exception as error:
            (directory/'status.json').write_text(json.dumps(dict(build_id=build_id,status='failed',compiler_success=None,cleanup_complete=True,exception=str(error))),encoding='utf8')
            raise
        return self.status(build_id)
