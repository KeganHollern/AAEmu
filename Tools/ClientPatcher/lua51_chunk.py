#!/usr/bin/env python3
"""Convert Lua 5.1 binary chunks between compatible scalar layouts.

ArcheAge r208022 uses little-endian Lua 5.1 chunks with 32-bit integers,
32-bit size_t values, 32-bit instructions, and 32-bit floating-point numbers.
Most modern Windows Lua 5.1 compilers emit 64-bit size_t values and 64-bit
numbers. This tool rewrites the chunk container without changing instructions.
"""

from __future__ import annotations

import argparse
import copy
import dataclasses
import hashlib
import io
import struct
from pathlib import Path
from typing import BinaryIO


LUA_SIGNATURE = b"\x1bLua"
LUA_VERSION_51 = 0x51

OPCODES = (
    "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL",
    "GETTABLE", "SETGLOBAL", "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF",
    "ADD", "SUB", "MUL", "DIV", "MOD", "POW", "UNM", "NOT", "LEN",
    "CONCAT", "JMP", "EQ", "LT", "LE", "TEST", "TESTSET", "CALL",
    "TAILCALL", "RETURN", "FORLOOP", "FORPREP", "TFORLOOP", "SETLIST",
    "CLOSE", "CLOSURE", "VARARG",
)

ABX_OPCODES = {"LOADK", "GETGLOBAL", "SETGLOBAL", "CLOSURE"}
ASBX_OPCODES = {"JMP", "FORLOOP", "FORPREP"}
RK_B_OPCODES = {"SETTABLE", "SELF", "ADD", "SUB", "MUL", "DIV", "MOD", "POW", "EQ", "LT", "LE"}
RK_C_OPCODES = {"GETTABLE", "SETTABLE", "SELF", "ADD", "SUB", "MUL", "DIV", "MOD", "POW", "EQ", "LT", "LE"}


@dataclasses.dataclass(frozen=True)
class ChunkFormat:
    little_endian: bool
    int_size: int
    size_t_size: int
    instruction_size: int
    number_size: int
    number_is_integral: bool

    @property
    def byte_order(self) -> str:
        return "<" if self.little_endian else ">"


@dataclasses.dataclass
class LuaConstant:
    tag: int
    value: None | bool | int | float | bytes


@dataclasses.dataclass
class LocalVariable:
    name: bytes | None
    start_pc: int
    end_pc: int


@dataclasses.dataclass
class Prototype:
    source: bytes | None
    line_defined: int
    last_line_defined: int
    upvalue_count: int
    parameter_count: int
    is_vararg: int
    max_stack_size: int
    code: list[int]
    constants: list[LuaConstant]
    children: list["Prototype"]
    line_info: list[int]
    local_variables: list[LocalVariable]
    upvalue_names: list[bytes | None]


class ChunkReader:
    def __init__(self, stream: BinaryIO):
        self.stream = stream
        self.format = self._read_header()

    def _read_exact(self, size: int) -> bytes:
        data = self.stream.read(size)
        if len(data) != size:
            raise ValueError(f"Unexpected end of chunk; wanted {size} bytes")
        return data

    def _read_header(self) -> ChunkFormat:
        if self._read_exact(4) != LUA_SIGNATURE:
            raise ValueError("Not a Lua binary chunk")
        version, chunk_format, endian, int_size, size_t_size, instruction_size, number_size, integral = struct.unpack(
            "8B", self._read_exact(8)
        )
        if version != LUA_VERSION_51 or chunk_format != 0:
            raise ValueError(f"Unsupported Lua chunk version/format: 0x{version:02x}/{chunk_format}")
        if endian not in (0, 1):
            raise ValueError(f"Invalid endian marker: {endian}")
        result = ChunkFormat(
            little_endian=endian == 1,
            int_size=int_size,
            size_t_size=size_t_size,
            instruction_size=instruction_size,
            number_size=number_size,
            number_is_integral=integral != 0,
        )
        if result.int_size not in (4, 8) or result.size_t_size not in (4, 8):
            raise ValueError(f"Unsupported integer layout: {result}")
        if result.instruction_size != 4:
            raise ValueError(f"Unsupported instruction size: {result.instruction_size}")
        if result.number_size not in (4, 8):
            raise ValueError(f"Unsupported number size: {result.number_size}")
        return result

    def _read_unsigned(self, size: int) -> int:
        return int.from_bytes(self._read_exact(size), "little" if self.format.little_endian else "big", signed=False)

    def _read_int(self) -> int:
        return int.from_bytes(
            self._read_exact(self.format.int_size),
            "little" if self.format.little_endian else "big",
            signed=True,
        )

    def _read_size_t(self) -> int:
        return self._read_unsigned(self.format.size_t_size)

    def _read_string(self) -> bytes | None:
        size = self._read_size_t()
        if size == 0:
            return None
        raw = self._read_exact(size)
        if raw[-1:] != b"\0":
            raise ValueError("Lua string is missing its trailing null byte")
        return raw[:-1]

    def _read_number(self) -> int | float:
        raw = self._read_exact(self.format.number_size)
        if self.format.number_is_integral:
            return int.from_bytes(raw, "little" if self.format.little_endian else "big", signed=True)
        code = "f" if self.format.number_size == 4 else "d"
        return struct.unpack(self.format.byte_order + code, raw)[0]

    def read_prototype(self) -> Prototype:
        source = self._read_string()
        line_defined = self._read_int()
        last_line_defined = self._read_int()
        upvalue_count, parameter_count, is_vararg, max_stack_size = struct.unpack("4B", self._read_exact(4))

        code = [self._read_unsigned(self.format.instruction_size) for _ in range(self._read_int())]

        constants: list[LuaConstant] = []
        for _ in range(self._read_int()):
            tag = self._read_exact(1)[0]
            if tag == 0:
                value = None
            elif tag == 1:
                value = self._read_exact(1)[0] != 0
            elif tag == 3:
                value = self._read_number()
            elif tag == 4:
                value = self._read_string()
            else:
                raise ValueError(f"Unsupported Lua constant tag: {tag}")
            constants.append(LuaConstant(tag, value))

        children = [self.read_prototype() for _ in range(self._read_int())]
        line_info = [self._read_int() for _ in range(self._read_int())]
        local_variables = [
            LocalVariable(self._read_string(), self._read_int(), self._read_int())
            for _ in range(self._read_int())
        ]
        upvalue_names = [self._read_string() for _ in range(self._read_int())]
        return Prototype(
            source=source,
            line_defined=line_defined,
            last_line_defined=last_line_defined,
            upvalue_count=upvalue_count,
            parameter_count=parameter_count,
            is_vararg=is_vararg,
            max_stack_size=max_stack_size,
            code=code,
            constants=constants,
            children=children,
            line_info=line_info,
            local_variables=local_variables,
            upvalue_names=upvalue_names,
        )


class ChunkWriter:
    def __init__(self, stream: BinaryIO, chunk_format: ChunkFormat):
        self.stream = stream
        self.format = chunk_format
        self._write_header()

    def _write_header(self) -> None:
        self.stream.write(LUA_SIGNATURE)
        self.stream.write(
            struct.pack(
                "8B",
                LUA_VERSION_51,
                0,
                1 if self.format.little_endian else 0,
                self.format.int_size,
                self.format.size_t_size,
                self.format.instruction_size,
                self.format.number_size,
                1 if self.format.number_is_integral else 0,
            )
        )

    def _write_integer(self, value: int, size: int, *, signed: bool) -> None:
        self.stream.write(
            int(value).to_bytes(size, "little" if self.format.little_endian else "big", signed=signed)
        )

    def _write_int(self, value: int) -> None:
        self._write_integer(value, self.format.int_size, signed=True)

    def _write_size_t(self, value: int) -> None:
        self._write_integer(value, self.format.size_t_size, signed=False)

    def _write_string(self, value: bytes | None) -> None:
        if value is None:
            self._write_size_t(0)
            return
        self._write_size_t(len(value) + 1)
        self.stream.write(value)
        self.stream.write(b"\0")

    def _write_number(self, value: int | float) -> None:
        if self.format.number_is_integral:
            self._write_integer(int(value), self.format.number_size, signed=True)
            return
        code = "f" if self.format.number_size == 4 else "d"
        self.stream.write(struct.pack(self.format.byte_order + code, float(value)))

    def write_prototype(self, prototype: Prototype) -> None:
        self._write_string(prototype.source)
        self._write_int(prototype.line_defined)
        self._write_int(prototype.last_line_defined)
        self.stream.write(
            struct.pack(
                "4B",
                prototype.upvalue_count,
                prototype.parameter_count,
                prototype.is_vararg,
                prototype.max_stack_size,
            )
        )

        self._write_int(len(prototype.code))
        for instruction in prototype.code:
            self._write_integer(instruction, self.format.instruction_size, signed=False)

        self._write_int(len(prototype.constants))
        for constant in prototype.constants:
            self.stream.write(bytes((constant.tag,)))
            if constant.tag == 1:
                self.stream.write(bytes((1 if constant.value else 0,)))
            elif constant.tag == 3:
                assert isinstance(constant.value, (int, float))
                self._write_number(constant.value)
            elif constant.tag == 4:
                assert constant.value is None or isinstance(constant.value, bytes)
                self._write_string(constant.value)

        self._write_int(len(prototype.children))
        for child in prototype.children:
            self.write_prototype(child)

        self._write_int(len(prototype.line_info))
        for line in prototype.line_info:
            self._write_int(line)

        self._write_int(len(prototype.local_variables))
        for local in prototype.local_variables:
            self._write_string(local.name)
            self._write_int(local.start_pc)
            self._write_int(local.end_pc)

        self._write_int(len(prototype.upvalue_names))
        for name in prototype.upvalue_names:
            self._write_string(name)


def read_chunk(path: Path) -> tuple[ChunkFormat, Prototype]:
    with path.open("rb") as stream:
        reader = ChunkReader(stream)
        prototype = reader.read_prototype()
        trailing = stream.read()
        if trailing:
            raise ValueError(f"Unexpected {len(trailing)} trailing bytes")
        return reader.format, prototype


def write_chunk(path: Path, chunk_format: ChunkFormat, prototype: Prototype) -> None:
    buffer = io.BytesIO()
    writer = ChunkWriter(buffer, chunk_format)
    writer.write_prototype(prototype)
    path.write_bytes(buffer.getvalue())


def describe_prototype(prototype: Prototype, name: str = "0", indent: str = "") -> None:
    source = prototype.source.decode("utf-8", "replace") if prototype.source else "(inherited)"
    print(
        f"{indent}{name}: lines {prototype.line_defined}-{prototype.last_line_defined}, "
        f"code={len(prototype.code)}, constants={len(prototype.constants)}, "
        f"children={len(prototype.children)}, source={source}"
    )
    for index, child in enumerate(prototype.children):
        describe_prototype(child, f"{name}_{index}", indent + "  ")


def list_string_constants(prototype: Prototype, name: str = "0") -> None:
    """Print every string constant with its owning prototype and constant slot."""
    for index, constant in enumerate(prototype.constants):
        if isinstance(constant.value, bytes):
            print(f"{name} K{index}: {constant.value.decode('utf-8', errors='replace')}")
    for index, child in enumerate(prototype.children):
        list_string_constants(child, f"{name}_{index}")


def strip_debug_info(prototype: Prototype) -> None:
    """Remove metadata that Lua does not need to execute the chunk."""
    prototype.line_info.clear()
    prototype.local_variables.clear()
    prototype.upvalue_names.clear()
    for child in prototype.children:
        strip_debug_info(child)


def get_prototype(prototype: Prototype, path: str) -> Prototype:
    parts = path.split("_")
    if not parts or parts[0] != "0":
        raise ValueError("Prototype path must start with 0 (for example: 0_3_1)")
    current = prototype
    for part in parts[1:]:
        index = int(part)
        if index < 0 or index >= len(current.children):
            raise ValueError(f"Prototype path does not exist: {path}")
        current = current.children[index]
    return current


def replace_prototype(prototype: Prototype, path: str, replacement: Prototype) -> None:
    parts = path.split("_")
    if len(parts) < 2 or parts[0] != "0":
        raise ValueError("Replacement prototype path must identify a child, such as 0_3")

    parent_path = "_".join(parts[:-1])
    child_index = int(parts[-1])
    parent = get_prototype(prototype, parent_path)
    if child_index < 0 or child_index >= len(parent.children):
        raise ValueError(f"Prototype path does not exist: {path}")

    original = parent.children[child_index]
    if replacement.upvalue_count != original.upvalue_count:
        raise ValueError(
            f"Prototype upvalue count mismatch: target has {original.upvalue_count}, "
            f"donor has {replacement.upvalue_count}"
        )

    transplanted = copy.deepcopy(replacement)
    transplanted.source = original.source
    parent.children[child_index] = transplanted


def encode_abc(opcode_name: str, a: int, b: int, c: int) -> int:
    opcode = OPCODES.index(opcode_name)
    return opcode | (a << 6) | (c << 14) | (b << 23)


def encode_abx(opcode_name: str, a: int, bx: int) -> int:
    opcode = OPCODES.index(opcode_name)
    return opcode | (a << 6) | (bx << 14)


def add_windowed_fullscreen_option_registration(prototype: Prototype) -> None:
    expected_constants = (
        b"id", b"ui_scale", b"default", None, b"funcDefaultValue", b"saveLevel",
        b"OL_SYSTEM", b"funcOnChanged", b"RegisterOptionItem",
    )
    for index, expected in enumerate(expected_constants):
        constant = prototype.constants[index]
        if expected is not None and constant.value != expected:
            raise ValueError(f"Unexpected screen_option.alb root constant K{index}: {constant.value!r}")

    if len(prototype.code) != 35 or (prototype.code[10] & 0x3F) != OPCODES.index("SETLIST"):
        raise ValueError("Unexpected screen_option.alb option-registration instruction layout")

    borderless_name_index = len(prototype.constants)
    prototype.constants.append(LuaConstant(4, b"r_FullscreenWindow"))
    zero_index = len(prototype.constants)
    prototype.constants.append(LuaConstant(3, 0.0))

    # Expand the descriptor array from one entry (ui_scale) to two entries.
    prototype.code[2] = encode_abc("NEWTABLE", 2, 2, 0)
    registration_code = [
        encode_abc("NEWTABLE", 4, 0, 3),
        encode_abc("SETTABLE", 4, 0x100 | 0, 0x100 | borderless_name_index),
        encode_abc("SETTABLE", 4, 0x100 | 2, 0x100 | zero_index),
        encode_abx("GETGLOBAL", 5, 6),
        encode_abc("SETTABLE", 4, 0x100 | 5, 5),
        encode_abc("SETLIST", 2, 2, 1),
    ]
    prototype.code = prototype.code[:10] + registration_code + prototype.code[11:]
    if prototype.line_info:
        prototype.line_info = prototype.line_info[:10] + [22] * len(registration_code) + prototype.line_info[11:]


def add_camera_option_registration(prototype: Prototype, donor_root: Prototype) -> None:
    expected_constants = (
        b"id", b"ui_scale", b"default", None, b"funcDefaultValue", b"saveLevel",
        b"OL_SYSTEM", b"funcOnChanged", b"RegisterOptionItem",
    )
    for index, expected in enumerate(expected_constants):
        constant = prototype.constants[index]
        if expected is not None and constant.value != expected:
            raise ValueError(f"Unexpected screen_option.alb root constant K{index}: {constant.value!r}")

    if len(donor_root.children) != 5:
        raise ValueError("Camera-control donor must contain four callbacks and one frame prototype")
    callbacks = donor_root.children[:4]
    if any(callback.upvalue_count != 0 for callback in callbacks):
        raise ValueError("Camera-control callbacks must not capture upvalues")

    replace_prototype(prototype, "0_11", donor_root.children[4])

    callback_child_indices = []
    for callback in callbacks:
        transplanted = copy.deepcopy(callback)
        transplanted.source = prototype.source
        callback_child_indices.append(len(prototype.children))
        prototype.children.append(transplanted)

    def add_string(value: bytes) -> int:
        index = len(prototype.constants)
        prototype.constants.append(LuaConstant(4, value))
        return index

    def add_number(value: float) -> int:
        index = len(prototype.constants)
        prototype.constants.append(LuaConstant(3, value))
        return index

    distance_option_index = add_string(b"AAEmuCameraMaxDistance")
    fov_option_index = add_string(b"AAEmuCameraFov")
    apply_distance_index = add_string(b"AAEmuApplyCameraMaxDistance")
    apply_fov_index = add_string(b"AAEmuApplyCameraFov")
    default_fov_index = add_string(b"AAEmuDefaultCameraFov")
    apply_persisted_index = add_string(b"AAEmuApplyPersistedCameraOptions")
    ui_parent_index = add_string(b"UIParent")
    set_event_handler_index = add_string(b"SetEventHandler")
    entered_world_index = add_string(b"ENTERED_WORLD")
    distance_default_index = add_number(35.0)
    fov_default_index = add_number(60.0)

    # Define the callbacks, register two named system-level option items, then
    # apply their persisted values once the game has registered its camera CVars.
    # In particular, camera_max_dist does not exist yet when this module loads.
    registration_code = [
        encode_abx("CLOSURE", 4, callback_child_indices[0]),
        encode_abx("SETGLOBAL", 4, apply_distance_index),
        encode_abx("CLOSURE", 4, callback_child_indices[1]),
        encode_abx("SETGLOBAL", 4, apply_fov_index),
        encode_abx("CLOSURE", 4, callback_child_indices[2]),
        encode_abx("SETGLOBAL", 4, default_fov_index),
        encode_abx("CLOSURE", 4, callback_child_indices[3]),
        encode_abx("SETGLOBAL", 4, apply_persisted_index),
        encode_abc("NEWTABLE", 4, 2, 0),
        encode_abc("NEWTABLE", 5, 0, 4),
        encode_abc("SETTABLE", 5, 0x100 | 0, 0x100 | distance_option_index),
        encode_abc("SETTABLE", 5, 0x100 | 2, 0x100 | distance_default_index),
        encode_abx("GETGLOBAL", 6, 6),
        encode_abc("SETTABLE", 5, 0x100 | 5, 6),
        encode_abc("NEWTABLE", 6, 0, 5),
        encode_abc("SETTABLE", 6, 0x100 | 0, 0x100 | fov_option_index),
        encode_abc("SETTABLE", 6, 0x100 | 2, 0x100 | fov_default_index),
        encode_abx("GETGLOBAL", 7, default_fov_index),
        encode_abc("SETTABLE", 6, 0x100 | 4, 7),
        encode_abx("GETGLOBAL", 7, 6),
        encode_abc("SETTABLE", 6, 0x100 | 5, 7),
        encode_abc("SETLIST", 4, 2, 1),
        encode_abx("GETGLOBAL", 5, 8),
        encode_abc("MOVE", 6, 4, 0),
        encode_abc("CALL", 5, 2, 1),
        encode_abx("GETGLOBAL", 5, ui_parent_index),
        encode_abc("SELF", 5, 5, 0x100 | set_event_handler_index),
        encode_abx("LOADK", 7, entered_world_index),
        encode_abx("GETGLOBAL", 8, apply_persisted_index),
        encode_abc("CALL", 5, 4, 1),
    ]

    # The fullscreen-window registration ends at instruction 18. Insert before
    # the stock control closures so their child indices and captures stay intact.
    insertion_index = 19
    if (
        len(prototype.code) <= insertion_index
        or (prototype.code[insertion_index] & 0x3F) != OPCODES.index("CLOSURE")
    ):
        raise ValueError("Unexpected patched screen_option.alb registration layout")
    prototype.code = prototype.code[:insertion_index] + registration_code + prototype.code[insertion_index:]
    if prototype.line_info:
        prototype.line_info = (
            prototype.line_info[:insertion_index]
            + [22] * len(registration_code)
            + prototype.line_info[insertion_index:]
        )
    prototype.max_stack_size = max(prototype.max_stack_size, 9)


def patch_hud_auction_button(prototype: Prototype) -> None:
    """Repair the r208022 HUD auction skin and use its existing locale entry."""
    if len(prototype.code) != 72 or len(prototype.constants) != 23:
        raise ValueError("Unexpected r208022 right_button_set.alb root layout")
    expected_root_constants = {
        20: b"UIParent",
        21: b"hudAuctionButton",
        22: b"auctionToggleBtn",
    }
    for index, expected in expected_root_constants.items():
        if prototype.constants[index].value != expected:
            raise ValueError(
                f"Unexpected right_button_set.alb root K{index}: "
                f"{prototype.constants[index].value!r}"
            )

    auction_factory = get_prototype(prototype, "0_6")
    if (
        len(auction_factory.code) != 38
        or auction_factory.constants[10].value != b"BUTTON_HUD"
        or auction_factory.constants[11].value != b"TOGGLE_AUCTION"
    ):
        raise ValueError("Unexpected r208022 auction-button factory layout")

    tooltip = get_prototype(prototype, "0_6_1")
    if (
        tooltip.parameter_count != 1
        or tooltip.max_stack_size != 6
        or len(tooltip.code) != 7
        or len(tooltip.constants) != 2
        or tooltip.constants[0].value != b"SetTooltip"
        or tooltip.constants[1].value != "경매장".encode("utf-8")
    ):
        raise ValueError("Unexpected r208022 auction-tooltip handler layout")

    def add_string(value: bytes) -> int:
        index = len(prototype.constants)
        prototype.constants.append(LuaConstant(4, value))
        return index

    def add_number(value: float) -> int:
        index = len(prototype.constants)
        prototype.constants.append(LuaConstant(3, value))
        return index

    button_hud_index = add_string(b"BUTTON_HUD")
    toggle_auction_index = add_string(b"TOGGLE_AUCTION")
    coords_index = add_string(b"coords")
    normal_index = add_string(b"normal")
    over_index = add_string(b"over")
    y_element_index = add_number(2.0)
    corrected_y_index = add_number(335.0)

    # The stock normal/hover rectangles start at Y=352/351, even though their
    # artwork is centered at Y=335. The pressed and disabled rectangles are
    # separate atlas cells and must remain at Y=351.
    skin_fix_code = [
        encode_abx("GETGLOBAL", 11, button_hud_index),
        encode_abc("GETTABLE", 11, 11, 0x100 | toggle_auction_index),
        encode_abc("GETTABLE", 11, 11, 0x100 | coords_index),
        encode_abc("GETTABLE", 12, 11, 0x100 | normal_index),
        encode_abc("SETTABLE", 12, 0x100 | y_element_index, 0x100 | corrected_y_index),
        encode_abc("GETTABLE", 12, 11, 0x100 | over_index),
        encode_abc("SETTABLE", 12, 0x100 | y_element_index, 0x100 | corrected_y_index),
    ]
    prototype.code = skin_fix_code + prototype.code
    if prototype.line_info:
        prototype.line_info = [1] * len(skin_fix_code) + prototype.line_info
    for local_variable in prototype.local_variables:
        local_variable.start_pc += len(skin_fix_code)
        local_variable.end_pc += len(skin_fix_code)

    # Resolve the localized value on hover. The cached locale.auction.auction
    # path is not populated yet when this HUD handler can first run.
    tooltip.constants = [
        LuaConstant(4, b"SetTooltip"),
        LuaConstant(4, b"X2Locale"),
        LuaConstant(4, b"LocalizeUiText"),
        LuaConstant(4, b"AUCTION_TEXT"),
        LuaConstant(4, b"auction_title"),
    ]
    tooltip.code = [
        encode_abx("GETGLOBAL", 1, 0),
        encode_abx("GETGLOBAL", 2, 1),
        encode_abc("SELF", 2, 2, 0x100 | 2),
        encode_abx("GETGLOBAL", 4, 3),
        encode_abx("LOADK", 5, 4),
        encode_abc("CALL", 2, 4, 2),
        encode_abc("MOVE", 3, 0, 0),
        encode_abc("LOADBOOL", 4, 0, 0),
        encode_abc("LOADBOOL", 5, 0, 0),
        encode_abc("CALL", 1, 5, 1),
        encode_abc("RETURN", 0, 1, 0),
    ]
    if tooltip.line_info:
        tooltip.line_info = [228] * 10 + [229]
    for local_variable in tooltip.local_variables:
        if local_variable.name == b"self":
            local_variable.end_pc = len(tooltip.code) - 1


def format_constant(constant: LuaConstant) -> str:
    if isinstance(constant.value, bytes):
        return repr(constant.value.decode("utf-8", "replace"))
    return repr(constant.value)


def format_rk(value: int, constants: list[LuaConstant]) -> str:
    if value & 0x100:
        index = value & 0xFF
        annotation = format_constant(constants[index]) if index < len(constants) else "<invalid>"
        return f"K{index}({annotation})"
    return f"R{value}"


def disassemble_prototype(prototype: Prototype, name: str) -> None:
    source = prototype.source.decode("utf-8", "replace") if prototype.source else "(inherited)"
    print(
        f"{name}: lines {prototype.line_defined}-{prototype.last_line_defined}, "
        f"params={prototype.parameter_count}, stack={prototype.max_stack_size}, source={source}"
    )
    print("constants:")
    for index, constant in enumerate(prototype.constants):
        print(f"  K{index:<3} {format_constant(constant)}")
    print("code:")
    for pc, instruction in enumerate(prototype.code):
        opcode = instruction & 0x3F
        a = (instruction >> 6) & 0xFF
        c = (instruction >> 14) & 0x1FF
        b = (instruction >> 23) & 0x1FF
        bx = (instruction >> 14) & 0x3FFFF
        sbx = bx - 131071
        name_text = OPCODES[opcode] if opcode < len(OPCODES) else f"OP_{opcode}"

        if name_text in ABX_OPCODES:
            operands = f"R{a} {bx}"
            if name_text == "CLOSURE":
                operands += f" (prototype {name}_{bx})"
            elif bx < len(prototype.constants):
                operands += f" ({format_constant(prototype.constants[bx])})"
        elif name_text in ASBX_OPCODES:
            operands = f"R{a} {sbx:+d} (to {pc + 1 + sbx})"
        else:
            b_text = format_rk(b, prototype.constants) if name_text in RK_B_OPCODES else str(b)
            c_text = format_rk(c, prototype.constants) if name_text in RK_C_OPCODES else str(c)
            operands = f"R{a} {b_text} {c_text}"

        line = prototype.line_info[pc] if pc < len(prototype.line_info) else 0
        print(f"  {pc:04d}  line {line:<4}  {name_text:<10} {operands}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    inspect_parser = subparsers.add_parser("inspect", help="Print chunk layout and prototype tree")
    inspect_parser.add_argument("input", type=Path)

    strings_parser = subparsers.add_parser("strings", help="Print all string constants in a chunk")
    strings_parser.add_argument("input", type=Path)

    disassemble_parser = subparsers.add_parser("disassemble", help="Print constants and Lua 5.1 instructions")
    disassemble_parser.add_argument("input", type=Path)
    disassemble_parser.add_argument("--prototype", default="0", help="Prototype path, such as 0_3_1")

    convert_parser = subparsers.add_parser("convert", help="Rewrite a chunk with a new scalar layout")
    convert_parser.add_argument("input", type=Path)
    convert_parser.add_argument("output", type=Path)
    convert_parser.add_argument("--int-size", type=int, choices=(4, 8))
    convert_parser.add_argument("--size-t-size", type=int, choices=(4, 8))
    convert_parser.add_argument("--number-size", type=int, choices=(4, 8))

    transplant_parser = subparsers.add_parser(
        "transplant",
        help="Replace one prototype in a chunk with a prototype compiled in another Lua 5.1 chunk",
    )
    transplant_parser.add_argument("input", type=Path, help="Chunk that supplies the surrounding module")
    transplant_parser.add_argument("donor", type=Path, help="Chunk that supplies the replacement prototype")
    transplant_parser.add_argument("output", type=Path)
    transplant_parser.add_argument("--target-prototype", required=True, help="Prototype path to replace")
    transplant_parser.add_argument("--donor-prototype", required=True, help="Prototype path to copy")

    screen_mode_parser = subparsers.add_parser(
        "patch-screen-mode",
        help="Build the r208022 three-way screen-mode module from a compiled donor prototype",
    )
    screen_mode_parser.add_argument("input", type=Path, help="Stock r208022 screen_option.alb")
    screen_mode_parser.add_argument("donor", type=Path, help="Compiled windowed-fullscreen Lua source")
    screen_mode_parser.add_argument("output", type=Path)

    camera_controls_parser = subparsers.add_parser(
        "patch-camera-controls",
        help="Add persistent camera-distance and FOV controls to a patched r208022 screen option module",
    )
    camera_controls_parser.add_argument("input", type=Path, help="Patched r208022 screen_option.alb")
    camera_controls_parser.add_argument("donor", type=Path, help="Compiled camera-control Lua source")
    camera_controls_parser.add_argument("output", type=Path)

    hud_auction_parser = subparsers.add_parser(
        "patch-hud-auction-button",
        help="Repair the r208022 HUD auction icon alignment and localized tooltip",
    )
    hud_auction_parser.add_argument("input", type=Path, help="Stock r208022 right_button_set.alb")
    hud_auction_parser.add_argument("output", type=Path)

    args = parser.parse_args()
    source_format, prototype = read_chunk(args.input)
    if args.command == "inspect":
        print(source_format)
        describe_prototype(prototype)
        return

    if args.command == "strings":
        list_string_constants(prototype)
        return

    if args.command == "disassemble":
        print(source_format)
        selected = get_prototype(prototype, args.prototype)
        disassemble_prototype(selected, args.prototype)
        return

    if args.command == "patch-hud-auction-button":
        patch_hud_auction_button(prototype)
        write_chunk(args.output, source_format, prototype)
        written_format, written_root = read_chunk(args.output)
        if written_format != source_format:
            raise ValueError("Written chunk format does not match the target chunk")
        written_tooltip = get_prototype(written_root, "0_6_1")
        if (
            len(written_root.code) != 79
            or len(written_tooltip.code) != 11
            or written_tooltip.constants[1].value != b"X2Locale"
            or written_tooltip.constants[4].value != b"auction_title"
        ):
            raise ValueError("Written HUD auction-button patch failed structural verification")
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest().upper()
        print(f"Patched r208022 HUD auction button -> {args.output} (SHA-256 {digest})")
        return

    if args.command == "transplant":
        donor_format, donor_root = read_chunk(args.donor)
        if donor_format.instruction_size != source_format.instruction_size:
            raise ValueError("Target and donor instruction sizes do not match")
        donor = get_prototype(donor_root, args.donor_prototype)
        replace_prototype(prototype, args.target_prototype, donor)
        write_chunk(args.output, source_format, prototype)
        written_format, written_root = read_chunk(args.output)
        if written_format != source_format:
            raise ValueError("Written chunk format does not match the target chunk")
        get_prototype(written_root, args.target_prototype)
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest().upper()
        print(
            f"Transplanted {args.donor_prototype} from {args.donor} into "
            f"{args.target_prototype} of {args.input} -> {args.output} (SHA-256 {digest})"
        )
        return

    if args.command == "patch-screen-mode":
        donor_format, donor_root = read_chunk(args.donor)
        if donor_format.instruction_size != source_format.instruction_size:
            raise ValueError("Target and donor instruction sizes do not match")
        donor = get_prototype(donor_root, "0_0")
        replace_prototype(prototype, "0_3", donor)
        add_windowed_fullscreen_option_registration(prototype)
        write_chunk(args.output, source_format, prototype)
        if args.output.stat().st_size > args.input.stat().st_size:
            # AAPacker replaces this archived module in place. Remove Lua's
            # execution-irrelevant debug tables when the richer control would
            # otherwise exceed the original storage slot.
            strip_debug_info(prototype)
            write_chunk(args.output, source_format, prototype)
        size_delta = args.input.stat().st_size - args.output.stat().st_size
        if size_delta > 0:
            # Keep the replacement exactly the same size as the archived module. A valid
            # debug-only local name supplies deterministic padding without adding trailing
            # bytes that stricter Lua loaders could reject.
            padding_name_length = size_delta - 13
            if padding_name_length < 1:
                raise ValueError(
                    f"Patched chunk is {size_delta} bytes smaller, which is insufficient for valid debug padding"
                )
            prefix = b"AAEMU_PADDING_"
            padding_name = (prefix + (b"_" * padding_name_length))[:padding_name_length]
            prototype.local_variables.append(LocalVariable(padding_name, 0, 0))
            write_chunk(args.output, source_format, prototype)
        if args.output.stat().st_size < args.input.stat().st_size:
            raise ValueError(
                f"Patched chunk size {args.output.stat().st_size} is smaller than target size {args.input.stat().st_size}"
            )
        written_format, written_root = read_chunk(args.output)
        if written_format != source_format:
            raise ValueError("Written chunk format does not match the target chunk")
        get_prototype(written_root, "0_3")
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest().upper()
        print(f"Built windowed-fullscreen screen option -> {args.output} (SHA-256 {digest})")
        return

    if args.command == "patch-camera-controls":
        donor_format, donor_root = read_chunk(args.donor)
        if donor_format.instruction_size != source_format.instruction_size:
            raise ValueError("Target and donor instruction sizes do not match")
        add_camera_option_registration(prototype, donor_root)
        write_chunk(args.output, source_format, prototype)
        if args.output.stat().st_size > args.input.stat().st_size:
            # The preceding screen-mode build may contain a large debug-only padding
            # local. Strip all execution-irrelevant debug tables before deciding
            # whether the combined module fits its original archive slot.
            strip_debug_info(prototype)
            write_chunk(args.output, source_format, prototype)
        size_delta = args.input.stat().st_size - args.output.stat().st_size
        if size_delta > 0:
            padding_name_length = size_delta - 13
            if padding_name_length < 1:
                raise ValueError(
                    f"Patched chunk is {size_delta} bytes smaller, which is insufficient for valid debug padding"
                )
            prefix = b"AAEMU_CAMERA_PADDING_"
            padding_name = (prefix + (b"_" * padding_name_length))[:padding_name_length]
            prototype.local_variables.append(LocalVariable(padding_name, 0, 0))
            write_chunk(args.output, source_format, prototype)
        if args.output.stat().st_size != args.input.stat().st_size:
            raise ValueError(
                f"Camera-control chunk size {args.output.stat().st_size} does not match target size "
                f"{args.input.stat().st_size}"
            )
        written_format, written_root = read_chunk(args.output)
        if written_format != source_format:
            raise ValueError("Written chunk format does not match the target chunk")
        get_prototype(written_root, "0_11")
        digest = hashlib.sha256(args.output.read_bytes()).hexdigest().upper()
        print(f"Built camera-control screen option -> {args.output} (SHA-256 {digest})")
        return

    target_format = dataclasses.replace(
        source_format,
        int_size=args.int_size or source_format.int_size,
        size_t_size=args.size_t_size or source_format.size_t_size,
        number_size=args.number_size or source_format.number_size,
    )
    write_chunk(args.output, target_format, prototype)
    print(f"Converted {args.input} ({source_format}) -> {args.output} ({target_format})")


if __name__ == "__main__":
    main()
