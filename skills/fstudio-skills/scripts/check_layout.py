"""只读检查完整画布快照与声明的布局约束；不连接宿主、不改工程、不自动修复。"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import sys


def number(value, minimum=None):
    """接受有限实数并核对可选下界；拒绝布尔值、NaN 和无穷，防止比较静默通过。"""
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError('Expected a finite number')
    if minimum is not None and value < minimum:
        raise ValueError(f'Expected number >= {minimum}')
    return value


def bounds(value):
    """读取统一矩形；零宽或零高可用于原生直线，不将其误判为非法图元。"""
    return {key: number(value[key], 0 if key in ('width', 'height') else None)
            for key in ('x', 'y', 'width', 'height')}


def merge_snapshots(snapshots):
    """合并同页连续的完整分页；缺页、筛选结果、重复身份和混页均作为输入错误拒绝。"""
    if not snapshots:
        raise ValueError('At least one snapshot is required')
    page = snapshots[0]['page']
    total = snapshots[0]['total_count']
    if isinstance(total, bool) or not isinstance(total, int) or total < 0:
        raise ValueError('Invalid total_count')
    if not page['uuid']:
        raise ValueError('Missing page UUID')
    number(page['width'], 1); number(page['height'], 1)
    slots = {}
    for snapshot in snapshots:
        if snapshot['page'] != page or snapshot['total_count'] != total:
            raise ValueError('Snapshots belong to different page states')
        if snapshot['scope'] != 'active_page_top_level_only' or snapshot['matched_count'] != total:
            raise ValueError('Provide unfiltered, top-level canvas snapshots')
        offset = snapshot['offset']; items = snapshot['items']
        if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0 or len(items) > 100:
            raise ValueError('Invalid snapshot pagination')
        expected_next = offset + len(items) if offset + len(items) < total else None
        if snapshot['next_offset'] != expected_next:
            raise ValueError('Inconsistent next_offset')
        for position, item in enumerate(items, offset):
            if position in slots or position >= total or item['index'] != position:
                raise ValueError('Overlapping or inconsistent snapshot pages')
            if not isinstance(item['name'], str) or not item['uuid']:
                raise ValueError('Invalid widget identity')
            bounds(item['bounds'])
            slots[position] = item
    if len(slots) != total:
        raise ValueError('Incomplete snapshots; fetch every next_offset before checking')
    items = [slots[index] for index in sorted(slots)]
    if len({item['uuid'] for item in items}) != len(items):
        raise ValueError('Duplicate widget UUIDs in snapshots')
    return page, items


def check(snapshots, plan):
    """比较稳定名称、类型、尺寸、公共层禁入名单及显式对齐/间距；返回可定位的问题列表。"""
    allowed = {'page_uuid', 'tolerance', 'expected', 'forbidden_names', 'align', 'gaps'}
    if set(plan) - allowed:
        raise ValueError('Unknown plan fields: ' + ', '.join(sorted(set(plan) - allowed)))
    for field in ('expected', 'forbidden_names', 'align', 'gaps'):
        if not isinstance(plan.get(field, []), list):
            raise ValueError(field + ' must be a list')
    if any(not isinstance(name, str) or not name for name in plan.get('forbidden_names', [])):
        raise ValueError('forbidden_names must contain nonempty exact names')
    page, items = merge_snapshots(snapshots)
    if plan['page_uuid'] != page['uuid']:
        raise ValueError('Plan page_uuid does not match snapshots')
    tolerance = number(plan.get('tolerance', 1), 0)
    issues = []; named = defaultdict(list)
    for item in items:
        named[item['name']].append(item)
    expected = plan.get('expected', [])
    if any(count > 1 for count in Counter(item['name'] for item in expected).values()):
        raise ValueError('Duplicate expected widget names')

    def issue(code, name, **details):
        """记录结构化差异；不以固定文案或画布外观推断运行行为。"""
        issues.append(dict(code=code, name=name, **details))

    def unique(name):
        """只返回唯一对象；缺失/重名时记录问题，禁止选择第一个对象继续计算。"""
        matches = named.get(name, [])
        if len(matches) != 1:
            issue('missing' if not matches else 'ambiguous', name, count=len(matches))
            return None
        return matches[0]

    for name, matches in named.items():
        if not name or len(matches) > 1:
            issue('unnamed' if not name else 'duplicate_name', name, count=len(matches))
    for name in plan.get('forbidden_names', []):
        if name in named:
            issue('forbidden_owner', name, count=len(named[name]))
    for wanted in expected:
        if set(wanted) - {'name', 'type', 'bounds'}:
            raise ValueError('Unknown expected-widget fields')
        item = unique(wanted['name'])
        if item is None:
            continue
        if 'type' in wanted and wanted['type'] != item['type']:
            issue('type', item['name'], expected=wanted['type'], actual=item['type'])
        if 'bounds' in wanted:
            for key, value in bounds(wanted['bounds']).items():
                actual = item['bounds'][key]
                if abs(actual - value) > tolerance:
                    issue('bounds', item['name'], member=key, expected=value, actual=actual)
    for rule in plan.get('align', []):
        if set(rule) != {'edge', 'names'} or len(rule['names']) < 2 or len(set(rule['names'])) != len(rule['names']):
            raise ValueError('Alignment requires an edge and at least two distinct names')
        axes = {'left': ('x', None), 'right': ('x', 'width'), 'top': ('y', None), 'bottom': ('y', 'height')}
        if rule['edge'] not in axes:
            raise ValueError('Unsupported alignment edge')
        selected = [unique(name) for name in rule['names']]
        if any(item is None for item in selected):
            continue
        axis, size = axes[rule['edge']]
        values = [item['bounds'][axis] + (item['bounds'][size] if size else 0) for item in selected]
        if max(values) - min(values) > tolerance:
            issue('alignment', rule['names'], edge=rule['edge'], values=values)
    for rule in plan.get('gaps', []):
        if set(rule) != {'axis', 'before', 'after', 'min'} or rule['axis'] not in ('x', 'y') or rule['before'] == rule['after']:
            raise ValueError('Gap requires axis x/y, distinct before/after names and min')
        minimum = number(rule['min'], 0)
        before, after = unique(rule['before']), unique(rule['after'])
        if before is None or after is None:
            continue
        axis = rule['axis']; size = 'width' if axis == 'x' else 'height'
        actual = after['bounds'][axis] - before['bounds'][axis] - before['bounds'][size]
        if actual + tolerance < minimum:
            issue('gap', [rule['before'], rule['after']], axis=axis, minimum=minimum, actual=actual)
    return {'ok': not issues, 'page_uuid': page['uuid'], 'checked_widgets': len(items),
            'issues': issues, 'scope': 'declared_top_level_geometry_only',
            'visual_and_runtime_verified': False}


def main():
    """读取 JSON 并输出结果；退出码 0 为无声明差异、1 为发现问题、2 为输入无效。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, action='append', required=True)
    parser.add_argument('--plan', type=Path, required=True)
    args = parser.parse_args()
    try:
        snapshots = [json.loads(path.read_text(encoding='utf-8-sig')) for path in args.snapshot]
        result = check(snapshots, json.loads(args.plan.read_text(encoding='utf-8-sig')))
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(json.dumps({'ok': False, 'input_error': str(error)}, ensure_ascii=False))
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result['ok'] else 1


if __name__ == '__main__':
    sys.exit(main())
