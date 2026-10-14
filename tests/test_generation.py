"""不启动 FStudio 的声明校验、合并及异步结果消费回归测试。"""
import copy
import io
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from canvas_spec import expand_widgets
from server import API_VERSION, BY_NAME, FStudio, validate, serve
from visible_host import VisibleHost


class GenerationTests(unittest.TestCase):
    def widget(self, **changes):
        """最小可用声明；每个测试独立创建，防止共享列表掩盖数据被修改。"""
        return dict(dict(name='caption', tool='Flexem.Studio.GraphicsDesigner.Tools.StaticTextTool',
                         text='测试文字', x=10, y=20, width=200, height=50), **changes)

    def test_preset_merges_paths_and_style_without_mutating_input(self):
        preset = {'base': {'text_style': {'bold': True, 'font_size': 18},
                           'properties': [{'member': 'A.B', 'value': 2}, {'member': 'C', 'value': 3}]}}
        widgets = [self.widget(preset='base', text_style={'font_size': 24},
                               properties=[{'member': 'A.B', 'value': 5}])]
        before = copy.deepcopy((widgets, preset))
        result = expand_widgets(widgets, preset)
        self.assertEqual(result[0]['text_style'], {'bold': True, 'font_size': 24})
        self.assertEqual(result[0]['properties'], [{'member': 'A.B', 'value': 5}, {'member': 'C', 'value': 3}])
        self.assertEqual((widgets, preset), before)
        self.assertNotIn('preset', result[0])

    def test_invalid_declarations_do_not_reach_host(self):
        service = FStudio.__new__(FStudio)
        service.visible = Mock()
        invalid = [
            [self.widget(), self.widget()],
            [self.widget(preset='missing')],
            [self.widget(properties=[{'member': 'Position.Width', 'value': 90}])],
            [self.widget(properties=[{'member': 'A', 'value': {'$ref': 'old'}}])],
            [self.widget(properties=[{'member': 'A', 'value': 1}, {'member': 'A', 'value': 2}])],
            [self.widget(text='   ')],
        ]
        for widgets in invalid:
            with self.subTest(widgets=widgets), self.assertRaises(ValueError):
                service.canvas_create_many(widgets)
        service.visible.request.assert_not_called()

    def test_preset_limit_applies_after_expansion(self):
        props = [{'member': f'P{i}', 'value': i} for i in range(41)]
        widgets = [dict(self.widget(preset='base'), name=f'item{i}') for i in range(30)]
        with self.assertRaisesRegex(ValueError, '1200'):
            expand_widgets(widgets, {'base': {'properties': props}})

    def test_schema_rejects_unknown_fields_nan_and_excess_presets(self):
        spec = BY_NAME['fstudio_canvas_create_many']['inputSchema']
        for args in ({'widgets': [dict(self.widget(), x=float('nan'))]},
                     {'widgets': [dict(self.widget(), typo=True)]},
                     {'widgets': [self.widget()], 'presets': {str(i): {} for i in range(21)}}):
            with self.subTest(args=args), self.assertRaises(ValueError):
                validate(args, spec)

    def test_uniform_write_rejects_expanded_limit_and_duplicate_refs(self):
        service = FStudio.__new__(FStudio)
        service.visible = Mock()
        for refs, props in ((['a']*2, [{'member': 'X', 'value': 1}]),
                            ([str(i) for i in range(51)], [{'member': 'X', 'value': 1}, {'member': 'Y', 'value': 2}])):
            with self.assertRaises(ValueError):
                service.model_apply_many(refs, props)
        service.visible.request.assert_not_called()

    def test_new_tools_preserve_mutation_annotations(self):
        for name in ('fstudio_canvas_create_many', 'fstudio_model_apply_many'):
            self.assertFalse(BY_NAME[name]['annotations']['readOnlyHint'])
            self.assertFalse(BY_NAME[name]['annotations']['idempotentHint'])
        self.assertTrue(BY_NAME['fstudio_canvas_snapshot']['annotations']['readOnlyHint'])
        # 公开版本只保留通用离线预览能力；项目专用动作不能残留在注册表或隐藏服务方法中。
        for suffix in ('validation_word_constants', 'simulator_navigate', 'simulator_mute'):
            self.assertNotIn('fstudio_' + suffix, BY_NAME)
            self.assertFalse(hasattr(FStudio, suffix))
        # 预览入口不暴露任意进程、窗口或坐标；输出只接受工作区文件，启动只接受编译身份。
        expected = {
            'fstudio_simulate_offline': set(),
            'fstudio_simulate_compiled': {'build_id'},
            'fstudio_canvas_render': {'file'},
            'fstudio_simulator_capture': {'file'},
            'fstudio_simulator_close': set(),
            'fstudio_simulator_helper_close': set(),
        }
        for name, fields in expected.items():
            self.assertEqual(set(BY_NAME[name]['inputSchema']['properties']), fields)

    def test_mcp_protocol_expands_preset_once_and_rejects_duplicates(self):
        """通过真实 stdio 分发链校验预设展开及错误返回，只替换最外侧原生传输。"""
        service = FStudio.__new__(FStudio)
        service.visible = Mock()
        service.visible.request.return_value = {'job_id': 'one', 'status': 'queued'}
        widgets = [self.widget(preset='base')]
        messages = [
            {'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2025-11-25'}},
            {'jsonrpc':'2.0','method':'notifications/initialized'},
            {'jsonrpc':'2.0','id':2,'method':'tools/list'},
            {'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'fstudio_canvas_create_many',
                'arguments':{'widgets':widgets,'presets':{'base':{'text_style':{'font_size':24}}}}}},
            {'jsonrpc':'2.0','id':4,'method':'tools/call','params':{'name':'fstudio_canvas_create_many',
                'arguments':{'widgets':[self.widget(),self.widget()]}}},
        ]
        output = io.StringIO()
        with patch('server.sys.stdin', io.StringIO('\n'.join(map(json.dumps,messages)))), patch('server.sys.stdout',output):
            serve(service)
        replies = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(replies[0]['result']['serverInfo']['version'], API_VERSION)
        self.assertEqual(API_VERSION, '0.5.0')
        # 工具会持续扩充；核对实际注册名称及唯一性，避免把旧版本数量误当成协议契约。
        listed = [item['name'] for item in replies[1]['result']['tools']]
        self.assertEqual(set(listed), set(BY_NAME))
        self.assertEqual(len(listed), len(set(listed)))
        self.assertFalse(replies[2]['result']['isError'])
        self.assertTrue(replies[3]['result']['isError'])
        service.visible.request.assert_called_once()
        self.assertEqual(service.visible.request.call_args.args, ('drawing_create_many',))
        expanded = service.visible.request.call_args.kwargs['widgets'][0]
        self.assertEqual(expanded['text_style'], {'font_size':24})
        self.assertNotIn('preset', expanded)


class JobConsumptionTests(unittest.TestCase):
    def test_inline_read_needs_no_job_poll(self):
        client = VisibleHost('unused')
        client.request = Mock(return_value={'job_id': 'one', 'status': 'completed', 'result': {'value': 42}})
        self.assertEqual(client.read('read_members', reads=[]), {'value': 42})
        client.request.assert_called_once_with('read_members', reads=[])

    def test_pending_read_polls_original_id_only(self):
        client = VisibleHost('unused')
        client.request = Mock(side_effect=[{'job_id': 'one', 'status': 'running'},
                                          {'job_id': 'one', 'status': 'completed', 'result': {'value': 42}}])
        self.assertEqual(client.read('read_members'), {'value': 42})
        self.assertEqual(client.request.call_count, 2)
        client.request.assert_called_with('job', job_id='one')

    def test_failed_inline_job_is_not_retried(self):
        client = VisibleHost('unused')
        client.request = Mock(return_value={'job_id': 'one', 'status': 'failed', 'error': 'native failure'})
        with self.assertRaisesRegex(RuntimeError, 'native failure'):
            client.read('read_members')
        self.assertEqual(client.request.call_count, 1)

    def test_service_wait_consumes_inline_result(self):
        service = FStudio.__new__(FStudio)
        service.visible = Mock()
        self.assertEqual(service._wait_job({'job_id': 'one', 'status': 'completed', 'result': {'saved': True}}), {'saved': True})
        service.visible.request.assert_not_called()

    def test_deadline_crossed_by_poll_still_consumes_terminal_reply(self):
        """最后一次查询跨过观察期限时，已收到的成功/失败优先于超时判断。"""
        for status in ('completed', 'failed'):
            terminal = {'job_id': 'one', 'status': status, 'result': {'value': 42}, 'error': 'late failure'}
            pending = {'job_id': 'one', 'status': 'running'}
            client = VisibleHost('unused')
            client.request = Mock(side_effect=[pending, terminal])
            with patch('visible_host.time.monotonic', side_effect=[0, 0, 1000]):
                if status == 'completed':
                    self.assertEqual(client.read('read_members'), {'value': 42})
                else:
                    with self.assertRaisesRegex(RuntimeError, 'late failure'):
                        client.read('read_members')
            service = FStudio.__new__(FStudio)
            service.visible = Mock()
            service.visible.request.return_value = terminal
            with patch('server.time.monotonic', side_effect=[0, 0, 1000]):
                if status == 'completed':
                    self.assertEqual(service._wait_job(pending), {'value': 42})
                else:
                    with self.assertRaisesRegex(RuntimeError, 'late failure'):
                        service._wait_job(pending)


if __name__ == '__main__':
    unittest.main()
