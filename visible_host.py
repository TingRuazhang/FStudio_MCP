"""Current-user named-pipe client for the in-process FStudio add-in."""
import ctypes
import json
from pathlib import Path
import queue
import threading
import time
import subprocess


class VisibleHost:
    def __init__(self, endpoint):
        self.endpoint = Path(endpoint)

    def start(self, bin_dir):
        if self.endpoint.is_file():
            endpoint=json.loads(self.endpoint.read_text(encoding='utf-8-sig'))
            try:return self.request('health')
            except RuntimeError:
                kernel=ctypes.WinDLL('kernel32',use_last_error=True)
                kernel.OpenProcess.restype=ctypes.c_void_p
                handle=kernel.OpenProcess(0x1000,False,int(endpoint['pid']))
                if handle:
                    kernel.CloseHandle.argtypes=[ctypes.c_void_p];kernel.CloseHandle(handle)
                    raise RuntimeError('Recorded host process still exists but pipe is unavailable. Inspect it; no duplicate instance started')
        addin=self.endpoint.parent.parent/'visible-addin'
        if not (addin/'FStudioMcp.addin').is_file():raise RuntimeError('Build local host using install-visible-host.ps1 first')
        exe=Path(bin_dir)/'FStudio.exe'
        if not exe.is_file():raise RuntimeError('FStudio executable missing')
        startup=subprocess.STARTUPINFO()
        startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow=4  # SW_SHOWNOACTIVATE: leave the user's current application focused.
        process=subprocess.Popen([str(exe),'-addindir:'+str(addin.resolve())],cwd=str(bin_dir),startupinfo=startup,creationflags=subprocess.CREATE_NO_WINDOW)
        return {'status':'starting','pid':process.pid,'visible':True,'instruction':'Poll visible_state; do not start another instance while this process is alive'}

    def request(self, op, **args):
        if not self.endpoint.is_file():
            raise RuntimeError('Visible FStudio host is not running; launch with the local add-in')
        endpoint = json.loads(self.endpoint.read_text(encoding='utf-8-sig'))
        pipe_name = endpoint['pipe']
        if pipe_name != 'fstudio-mcp-' + str(endpoint['pid']): raise RuntimeError('Invalid host endpoint')
        path = '\\\\.\\pipe\\' + pipe_name
        wait_pipe = ctypes.WinDLL('kernel32', use_last_error=True).WaitNamedPipeW
        wait_pipe.argtypes = [ctypes.c_wchar_p,ctypes.c_uint32]
        wait_pipe.restype = ctypes.c_int
        # The server recreates its single pipe instance after each response.
        # WaitNamedPipe returns immediately for ERROR_FILE_NOT_FOUND in that gap.
        deadline=time.monotonic()+2
        while not wait_pipe(path,max(1,int((deadline-time.monotonic())*1000))):
            error=ctypes.get_last_error()
            if error not in (2,121,231) or time.monotonic()>=deadline:
                raise RuntimeError('Visible host pipe is unavailable; endpoint may be stale')
            time.sleep(.01)
        responses = queue.Queue()
        def transfer():
            try:
                with open(path,'r+b',buffering=8192) as stream:
                    stream.write(json.dumps(dict(args,op=op),ensure_ascii=False,allow_nan=False).encode('utf8')+b'\n');stream.flush()
                    raw = stream.readline(8*1024*1024)
                    responses.put(json.loads(raw))
            except Exception as e: responses.put(e)
        threading.Thread(target=transfer,daemon=True).start()
        try: result = responses.get(timeout=10)
        except queue.Empty: raise RuntimeError('Host response timed out; outcome is unknown. Inspect host state before retrying')
        if isinstance(result,Exception): raise result
        if not result.get('ok'): raise RuntimeError(result.get('error','Host call failed'))
        return result['data']

    def read(self, op, **args):
        job = self.request(op,**args)
        deadline = time.monotonic()+5
        while time.monotonic()<deadline:
            result = self.request('job',job_id=job['job_id'])
            if result['status']=='completed': return result['result']
            if result['status']=='failed': raise RuntimeError(result['error'])
            time.sleep(.1)
        return job
