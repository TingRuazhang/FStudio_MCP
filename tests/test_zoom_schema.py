"""验证数值运算接口的有符号参数边界；不创建客户端或访问原生宿主。"""
import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from server import BY_NAME, validate


class ZoomSchemaTests(unittest.TestCase):
    def test_signed_numeric_parameters(self):
        """负倍率和负偏移属于合法业务配置，不应被画布坐标的非负范围拦截。"""
        spec = BY_NAME["fstudio_numeric_configure_zoom"]["inputSchema"]
        validate({"ref": "draft-model", "gain": -0.1, "offset": -300, "decimal_scaling": False}, spec)

    def test_reject_invalid_numeric_values(self):
        """有符号不代表宽松类型；非有限数、布尔和字符串仍须在进入宿主之前拒绝。"""
        spec = BY_NAME["fstudio_numeric_configure_zoom"]["inputSchema"]
        for key in ("gain", "offset"):
            for value in (math.nan, math.inf, -math.inf, True, "-1"):
                with self.subTest(key=key, value=value):
                    arguments = {"ref": "draft-model", "gain": 0.1, "offset": 0, "decimal_scaling": False}
                    arguments[key] = value
                    with self.assertRaises(ValueError):
                        validate(arguments, spec)


if __name__ == "__main__":
    unittest.main()
