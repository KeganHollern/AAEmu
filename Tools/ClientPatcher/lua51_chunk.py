#!/usr/bin/env python3
"""Convert Lua 5.1 binary chunks between compatible scalar layouts.

ArcheAge r208022 uses little-endian Lua 5.1 chunks with 32-bit integers,
32-bit size_t values, 32-bit instructions, and 32-bit floating-point numbers.
Most modern Windows Lua 5.1 compilers emit 64-bit size_t values and 64-bit
numbers. This tool rewrites the chunk container without changing instructions.
"""

from __future__ import annotations

import argparse
import dataclasses
import io
import struct
from pathlib import Path
from typing import BinaryIO


LUA_SIGNATURE = b"\x1bLua"
LUA_VERSION_51 = 0x51


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


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    inspect_parser = subparsers.add_parser("inspect", help="Print chunk layout and prototype tree")
    inspect_parser.add_argument("input", type=Path)

    convert_parser = subparsers.add_parser("convert", help="Rewrite a chunk with a new scalar layout")
    convert_parser.add_argument("input", type=Path)
    convert_parser.add_argument("output", type=Path)
    convert_parser.add_argument("--int-size", type=int, choices=(4, 8))
    convert_parser.add_argument("--size-t-size", type=int, choices=(4, 8))
    convert_parser.add_argument("--number-size", type=int, choices=(4, 8))

    args = parser.parse_args()
    source_format, prototype = read_chunk(args.input)
    if args.command == "inspect":
        print(source_format)
        describe_prototype(prototype)
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
