"""把真实批量宿主的内存契约检查纳入统一测试入口。"""
import os
from pathlib import Path
import subprocess
import shutil
import unittest


@unittest.skipUnless(os.name == 'nt', '原生契约检查需要 Windows .NET Framework')
class DrawingContractsTests(unittest.TestCase):
    def test_drawing_contracts(self):
        """加载已构建插件测试分页、编号与回滚边界，不启动 FStudio 或打开用户工程。"""
        root = Path(__file__).resolve().parents[1]
        host = root/'artifacts/FStudioMcp.Visible.dll'
        if not host.is_file():
            self.skipTest('请先运行 install-visible-host.ps1 构建当前宿主 DLL')
        # 与项目安装/构建入口一致使用 PowerShell 7，避免旧 Windows PowerShell 的本机程序调用差异。
        shell = shutil.which('pwsh')
        if not shell:
            self.skipTest('请先安装项目所需的 PowerShell 7')
        command = [shell, '-NoLogo', '-NoProfile', '-File', str(root/'tests/run_drawing_contracts.ps1'), '-HostDll', str(host)]
        result = subprocess.run(command, cwd=root, capture_output=True, timeout=60)
        output = (result.stdout+result.stderr).decode('utf8', errors='replace')
        self.assertEqual(result.returncode, 0, output)
        self.assertIn('Drawing contract checks passed: 55', output)
        self.assertIn('Preview build ownership checks passed: 15', output)


if __name__ == '__main__':
    unittest.main()
