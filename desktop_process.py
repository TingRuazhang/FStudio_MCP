"""Owned Windows process on a private desktop; never switch the input desktop.

This isolates native compiler windows, not filesystem access or permissions.
"""
import ctypes
from ctypes import wintypes as W
from pathlib import Path
import subprocess
import uuid


class StartupInfo(ctypes.Structure):
    _fields_=[('cb',W.DWORD),('lpReserved',W.LPWSTR),('lpDesktop',W.LPWSTR),
              ('lpTitle',W.LPWSTR),('dwX',W.DWORD),('dwY',W.DWORD),
              ('dwXSize',W.DWORD),('dwYSize',W.DWORD),('dwXCountChars',W.DWORD),
              ('dwYCountChars',W.DWORD),('dwFillAttribute',W.DWORD),
              ('dwFlags',W.DWORD),('wShowWindow',W.WORD),('cbReserved2',W.WORD),
              ('lpReserved2',ctypes.POINTER(ctypes.c_ubyte)),('hStdInput',W.HANDLE),
              ('hStdOutput',W.HANDLE),('hStdError',W.HANDLE)]


class ProcessInfo(ctypes.Structure):
    _fields_=[('hProcess',W.HANDLE),('hThread',W.HANDLE),
              ('dwProcessId',W.DWORD),('dwThreadId',W.DWORD)]


class DesktopProcess:
    def __init__(self, executable, arguments=(), *, cwd):
        self.kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        self.user=ctypes.WinDLL('user32',use_last_error=True)
        self.desktop=None
        self.process=None
        self.pid=None
        self.name='FStudioMcpBuild_'+uuid.uuid4().hex
        self.kernel.CloseHandle.argtypes=[W.HANDLE]
        self.kernel.WaitForSingleObject.argtypes=[W.HANDLE,W.DWORD]
        self.kernel.WaitForSingleObject.restype=W.DWORD
        self.kernel.GetExitCodeProcess.argtypes=[W.HANDLE,ctypes.POINTER(W.DWORD)]
        self.kernel.TerminateProcess.argtypes=[W.HANDLE,W.UINT]
        self.user.CloseDesktop.argtypes=[W.HANDLE]
        self.user.CreateDesktopW.argtypes=[W.LPCWSTR,W.LPCWSTR,ctypes.c_void_p,W.DWORD,W.DWORD,ctypes.c_void_p]
        self.user.CreateDesktopW.restype=W.HANDLE
        self.kernel.CreateProcessW.argtypes=[W.LPCWSTR,W.LPWSTR,ctypes.c_void_p,ctypes.c_void_p,W.BOOL,W.DWORD,ctypes.c_void_p,W.LPCWSTR,ctypes.POINTER(StartupInfo),ctypes.POINTER(ProcessInfo)]
        self.kernel.CreateProcessW.restype=W.BOOL
        exe=Path(executable).resolve(strict=True)
        directory=Path(cwd).resolve(strict=True)
        if not exe.is_file() or not directory.is_dir():raise ValueError('Expected executable and working directory')
        # CREATEWINDOW, READOBJECTS, ENUMERATE, WRITEOBJECTS. No SWITCHDESKTOP.
        self.desktop=self.user.CreateDesktopW(self.name,None,None,0,0xC3,None)
        if not self.desktop:raise ctypes.WinError(ctypes.get_last_error())
        startup=StartupInfo()
        startup.cb=ctypes.sizeof(startup)
        startup.lpDesktop=self.name
        startup.dwFlags=1  # STARTF_USESHOWWINDOW
        startup.wShowWindow=4  # Initialize WPF normally, only on the private desktop.
        info=ProcessInfo()
        command=ctypes.create_unicode_buffer(subprocess.list2cmdline([str(exe),*map(str,arguments)]))
        try:
            if not self.kernel.CreateProcessW(str(exe),command,None,None,False,0x08000000,None,str(directory),ctypes.byref(startup),ctypes.byref(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            self.process=info.hProcess
            self.pid=info.dwProcessId
            self.kernel.CloseHandle(info.hThread)
        except BaseException:
            self.close()
            raise

    def poll(self):
        if not self.process:raise RuntimeError('Process handle closed')
        status=self.kernel.WaitForSingleObject(self.process,0)
        if status==258:return None
        if status!=0:raise ctypes.WinError(ctypes.get_last_error())
        code=W.DWORD()
        if not self.kernel.GetExitCodeProcess(self.process,ctypes.byref(code)):raise ctypes.WinError(ctypes.get_last_error())
        return code.value

    def windows(self):
        """Read only this worker's top-level window metadata on its desktop."""
        result=[]
        callback_type=ctypes.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
        self.user.GetWindowThreadProcessId.argtypes=[W.HWND,ctypes.POINTER(W.DWORD)]
        self.user.GetWindowTextW.argtypes=[W.HWND,W.LPWSTR,ctypes.c_int]
        self.user.GetClassNameW.argtypes=[W.HWND,W.LPWSTR,ctypes.c_int]
        self.user.IsWindowVisible.argtypes=[W.HWND]
        @callback_type
        def collect(handle,unused):
            pid=W.DWORD()
            self.user.GetWindowThreadProcessId(handle,ctypes.byref(pid))
            if pid.value==self.pid:
                title=ctypes.create_unicode_buffer(1024)
                kind=ctypes.create_unicode_buffer(256)
                self.user.GetWindowTextW(handle,title,len(title))
                self.user.GetClassNameW(handle,kind,len(kind))
                row=dict(hwnd=handle,title=title.value,window_class=kind.value,visible=bool(self.user.IsWindowVisible(handle)))
                if kind.value=='#32770':
                    children=[]
                    @callback_type
                    def child_text(child,unused):
                        buffer=ctypes.create_unicode_buffer(4096)
                        self.user.GetWindowTextW(child,buffer,len(buffer))
                        if buffer.value:children.append(buffer.value)
                        return len(children)<32
                    self.user.EnumChildWindows.argtypes=[W.HWND,callback_type,W.LPARAM]
                    self.user.EnumChildWindows(handle,child_text,0)
                    row['child_text']=children
                result.append(row)
            return True
        self.user.EnumDesktopWindows.argtypes=[W.HANDLE,callback_type,W.LPARAM]
        self.user.EnumDesktopWindows.restype=W.BOOL
        if not self.user.EnumDesktopWindows(self.desktop,collect,0):raise ctypes.WinError(ctypes.get_last_error())
        return result

    def close(self):
        if self.process:
            if self.poll() is None:
                if not self.kernel.TerminateProcess(self.process,1):raise ctypes.WinError(ctypes.get_last_error())
                if self.kernel.WaitForSingleObject(self.process,5000)!=0:raise RuntimeError('Owned worker did not terminate')
            self.kernel.CloseHandle(self.process)
            self.process=None
        if self.desktop:
            if not self.user.CloseDesktop(self.desktop):raise ctypes.WinError(ctypes.get_last_error())
            self.desktop=None

    def __enter__(self):return self
    def __exit__(self,*unused):self.close()
