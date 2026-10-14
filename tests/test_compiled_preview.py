"""在独立临时工程中验证归档模拟的 MCP 服务端边界，不连接宿主或运行模拟器。"""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build_snapshot import input_manifest
from server import FStudio, serve


class CompiledPreviewTests(unittest.TestCase):
    """仅模拟最外层原生传输，源码和输出哈希仍走真实文件清单实现。"""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='fstudio-compiled-tests-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.project = self.root/'workspace/example/test.fsprj'
        self.project.parent.mkdir(parents=True)
        self.project.write_bytes(b'project-source')
        configuration = self.project.parent/'HMI/System/ConnectInfo.cfg'
        configuration.parent.mkdir(parents=True)
        configuration.write_bytes(b'configuration-source')
        self.bin = self.project.parent/'Bin'
        self.bin.mkdir()
        (self.bin/'fshmi.bin').write_bytes(b'previous-output')
        self.records = self.project.parent/'temp/disk/hmi/records.dat'
        self.records.parent.mkdir(parents=True)
        self.records.write_bytes(b'records-must-be-preserved')
        self.job = self.root/'job'
        self.outputs = self.job/'outputs'
        self.outputs.mkdir(parents=True)
        (self.outputs/'fshmi.bin').write_bytes(b'verified-output')
        driver = self.outputs/'Device/driver.dat'
        driver.parent.mkdir()
        driver.write_bytes(b'verified-driver')
        self.build_id = 'a'*32
        self.state = dict(status='completed', compiler_success=True, project=str(self.project),
                          source_unchanged=True, cleanup_complete=True)
        self.write_evidence()
        self.service = FStudio.__new__(FStudio)
        self.service.workspace = self.root/'workspace'
        self.service.builds = Mock()
        self.service.builds.status.return_value = self.state
        self.service.builds.directory.return_value = self.job
        self.service.visible = Mock()
        self.service.visible.request.side_effect = self.host_reply

    def host_reply(self, operation, **arguments):
        """只提供当前工程的只读预检和启动请求结果；没有真实进程或 GUI 输入。"""
        if operation == 'task_compiled_preview_preflight':
            return {'project': str(self.project), 'simulators_running': False, 'dirty_views': 0}
        if operation == 'task_simulate_compiled_offline':
            return {'job_id': 'start', 'status': 'queued'}
        raise AssertionError('Unexpected native request: ' + operation)

    def write_evidence(self):
        """模拟已成功归档的原生编译证据；不替代被测代码中的当前清单比较。"""
        (self.job/'snapshot.json').write_text(json.dumps({
            'source': str(self.project), 'inputs': input_manifest(self.project.parent)}), encoding='utf8')
        (self.job/'outputs.json').write_text(json.dumps({
            'files': input_manifest(self.outputs)}), encoding='utf8')

    def assert_not_started(self):
        self.assertFalse(any(call.args[0] == 'task_simulate_compiled_offline'
                             for call in self.service.visible.request.call_args_list))

    def test_stdio_stages_verified_outputs_and_preserves_records(self):
        """通过真实 MCP 分发入口复用完整编译输出，同时保留 temp 与未列出的历史输出。"""
        (self.bin/'history.dat').write_bytes(b'previous-generated-file')
        message = {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {
            'name': 'fstudio_simulate_compiled', 'arguments': {'build_id': self.build_id}}}
        messages = [
            {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize',
             'params': {'protocolVersion': '2025-11-25'}},
            {'jsonrpc': '2.0', 'method': 'notifications/initialized'}, message,
        ]
        output = io.StringIO()
        with patch('server.sys.stdin', io.StringIO('\n'.join(map(json.dumps, messages)))), patch('server.sys.stdout', output):
            serve(self.service)
        replies = [json.loads(line) for line in output.getvalue().splitlines()]
        reply = replies[-1]['result']
        self.assertFalse(reply['isError'])
        self.assertEqual((self.bin/'fshmi.bin').read_bytes(), b'verified-output')
        self.assertEqual((self.bin/'Device/driver.dat').read_bytes(), b'verified-driver')
        self.assertEqual((self.bin/'history.dat').read_bytes(), b'previous-generated-file')
        self.assertEqual(self.records.read_bytes(), b'records-must-be-preserved')

    def test_saved_database_change_requires_rebuild(self):
        """重开工程改变原生数据库后，旧 source_unchanged=true 不能替代当前源码哈希。"""
        (self.project.parent/'HMI/System/fs2.s3db').write_bytes(b'changed-after-build')
        with self.assertRaisesRegex(ValueError, 'inputs differ'):
            self.service.simulate_compiled(self.build_id)
        self.assertEqual((self.bin/'fshmi.bin').read_bytes(), b'previous-output')
        self.assert_not_started()

    def test_tampered_archive_is_rejected_before_overwrite(self):
        """成功状态不能掩盖归档文件发生变化；旧 Bin 在任何复制之前保持不变。"""
        (self.outputs/'fshmi.bin').write_bytes(b'tampered-output')
        with self.assertRaisesRegex(ValueError, 'output hashes'):
            self.service.simulate_compiled(self.build_id)
        self.assertEqual((self.bin/'fshmi.bin').read_bytes(), b'previous-output')
        self.assert_not_started()

    def test_another_active_project_is_rejected(self):
        """编译必须属于预检返回的当前工程，不能仅凭同一工作区复用其他工程输出。"""
        self.state['project'] = str(self.project.parent/'other.fsprj')
        with self.assertRaisesRegex(ValueError, 'another active project'):
            self.service.simulate_compiled(self.build_id)
        self.assert_not_started()

    def test_compiler_failure_does_not_reach_host(self):
        """completed 与 compiler_success 分别核验，失败编译不能发出原生预检或启动请求。"""
        self.state['compiler_success'] = False
        with self.assertRaisesRegex(ValueError, 'successful native build'):
            self.service.simulate_compiled(self.build_id)
        self.service.visible.request.assert_not_called()

    def test_existing_simulator_blocks_copy(self):
        """原生预检发现既有模拟器时，服务器不得覆盖正在使用的生成文件。"""
        self.service.visible.request.side_effect = RuntimeError('An existing FSGUI process must be closed')
        with self.assertRaisesRegex(RuntimeError, 'existing FSGUI'):
            self.service.simulate_compiled(self.build_id)
        self.assertEqual((self.bin/'fshmi.bin').read_bytes(), b'previous-output')
        self.assert_not_started()

    def test_copy_failure_never_starts_simulator(self):
        """生成文件复制异常必须向调用方失败返回，不启动包含部分新输出的模拟器。"""
        with patch('server.shutil.copyfile', side_effect=OSError('sharing violation')):
            with self.assertRaisesRegex(OSError, 'sharing violation'):
                self.service.simulate_compiled(self.build_id)
        self.assert_not_started()
        self.assertEqual(self.records.read_bytes(), b'records-must-be-preserved')

    def test_internal_link_is_rejected_before_any_copy(self):
        """Bin 文件链接在覆盖旧输出之前拒绝，避免先通过链接写入再在末尾扫描才发现。"""
        target = self.bin/'target'
        target.mkdir()
        (target/'keep.dat').write_bytes(b'keep-target')
        link = self.bin/'link'
        junction = False
        try:
            os.symlink(target, link, target_is_directory=True)
        except OSError as error:
            if os.name != 'nt':
                self.skipTest('系统未授予临时符号链接创建权限：' + str(error))
            # Windows 普通用户可创建目录联接点，同样覆盖原生 reparse 标志检测而无需提升权限。
            result = subprocess.run(['cmd.exe', '/c', 'mklink', '/J', str(link), str(target)],
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
            junction = True
        try:
            with self.assertRaisesRegex(ValueError, 'reparse points'):
                self.service.simulate_compiled(self.build_id)
            self.assertEqual((self.bin/'fshmi.bin').read_bytes(), b'previous-output')
            self.assertEqual((target/'keep.dat').read_bytes(), b'keep-target')
            self.assert_not_started()
        finally:
            if junction:
                os.rmdir(link)
            else:
                link.unlink()

    @unittest.skipUnless(os.name == 'nt', '目录联接点检查只适用于 Windows')
    def test_bin_junction_to_source_is_rejected(self):
        """拒绝将 Bin 联接到工程内 HMI；解析结果仍在工程内也不允许写入源码目录。"""
        (self.bin/'fshmi.bin').unlink()
        self.bin.rmdir()
        source = self.project.parent/'HMI'
        # 仅创建临时测试工作区内的固定链接；清理由临时目录完成，不枚举或传入用户路径。
        result = subprocess.run(['cmd.exe', '/c', 'mklink', '/J', str(self.bin), str(source)],
                                capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        try:
            with self.assertRaisesRegex(ValueError, 'reparse points'):
                self.service.simulate_compiled(self.build_id)
            self.assertFalse((source/'fshmi.bin').exists())
            self.assert_not_started()
        finally:
            # unlink 不适用于目录联接点；rmdir 仅去掉链接本身，不递归删除其目标 HMI。
            os.rmdir(self.bin)


if __name__ == '__main__':
    unittest.main()
