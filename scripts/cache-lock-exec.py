#!/usr/bin/env python3
"""Open and hold the persistent build-cache lock without following links.

This helper is intentionally small.  Shell redirections follow a final
symlink, so build-container.sh delegates creation/opening of cache.lock to
openat(2) with O_NOFOLLOW and keeps that descriptor across exec.
"""

from __future__ import annotations

import fcntl
import os
import stat
import sys


EXPECTED_DIR_MODE = 0o700
EXPECTED_LOCK_MODE = 0o600


def fail(message: str) -> "NoReturn":
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def validate_name(name: str, label: str) -> None:
    if not name or name in {".", ".."} or "/" in name or "\x00" in name:
        fail(f"unsafe {label} component")


def validate_dir_stat(value: os.stat_result, label: str) -> None:
    if not stat.S_ISDIR(value.st_mode):
        fail(f"{label} is not a directory")
    if value.st_uid != os.getuid():
        fail(f"{label} is not owned by the invoking user")
    if stat.S_IMODE(value.st_mode) != EXPECTED_DIR_MODE:
        fail(f"{label} must have mode 0700")


def open_root(path: str) -> int:
    if not os.path.isabs(path) or os.path.normpath(path) != path:
        fail("persistent cache root must be canonical and absolute")
    try:
        before = os.lstat(path)
    except FileNotFoundError:
        fail("persistent cache root must exist before cache preparation")
    validate_dir_stat(before, "persistent cache root")
    if os.path.realpath(path) != path:
        fail("persistent cache root contains or is a symlink")
    try:
        descriptor = os.open(
            path,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
        )
    except OSError as error:
        fail(f"cannot safely open persistent cache root: {error}")
    after = os.fstat(descriptor)
    validate_dir_stat(after, "persistent cache root")
    if (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
        fail("persistent cache root changed while opening")
    return descriptor


def open_dir_at(parent: int, name: str, label: str, create: bool) -> int:
    validate_name(name, label)
    if create:
        try:
            os.mkdir(name, EXPECTED_DIR_MODE, dir_fd=parent)
        except FileExistsError:
            pass
        except OSError as error:
            fail(f"cannot create {label}: {error}")
    try:
        before = os.stat(name, dir_fd=parent, follow_symlinks=False)
        descriptor = os.open(
            name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
            dir_fd=parent,
        )
    except OSError as error:
        fail(f"cannot safely open {label}: {error}")
    after = os.fstat(descriptor)
    validate_dir_stat(before, label)
    validate_dir_stat(after, label)
    if (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
        fail(f"{label} changed while opening")
    return descriptor


def open_cache_tree(root_path: str, cache_hash: str, create: bool) -> tuple[list[int], int]:
    if len(cache_hash) != 64 or any(character not in "0123456789abcdef" for character in cache_hash):
        fail("invalid cache-input identity")
    descriptors = [open_root(root_path)]
    for name, label in (
        ("ghelper-x13-build", "build-cache directory"),
        (cache_hash, "cache-input directory"),
        ("nuget", "NuGet cache directory"),
    ):
        descriptors.append(open_dir_at(descriptors[-1], name, label, create))
        if name == cache_hash:
            cache_descriptor = descriptors[-1]
    # dotnet is a sibling of nuget, not its child.
    descriptors.append(open_dir_at(cache_descriptor, "dotnet", "dotnet cache directory", create))
    return descriptors, cache_descriptor


def validate_lock(descriptor: int, cache_descriptor: int) -> None:
    try:
        path_stat = os.stat("cache.lock", dir_fd=cache_descriptor, follow_symlinks=False)
        fd_stat = os.fstat(descriptor)
    except OSError as error:
        fail(f"cannot validate cache.lock: {error}")
    for value in (path_stat, fd_stat):
        if not stat.S_ISREG(value.st_mode):
            fail("cache.lock is not a regular file")
        if value.st_uid != os.getuid():
            fail("cache.lock is not owned by the invoking user")
        if stat.S_IMODE(value.st_mode) != EXPECTED_LOCK_MODE:
            fail("cache.lock must have mode 0600")
        if value.st_nlink != 1:
            fail("cache.lock must have exactly one link")
    if (path_stat.st_dev, path_stat.st_ino) != (fd_stat.st_dev, fd_stat.st_ino):
        fail("cache.lock changed while opening")


def create_and_open_lock(cache_descriptor: int) -> int:
    flags = os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        descriptor = os.open(
            "cache.lock",
            flags | os.O_CREAT | os.O_EXCL,
            EXPECTED_LOCK_MODE,
            dir_fd=cache_descriptor,
        )
        os.fchmod(descriptor, EXPECTED_LOCK_MODE)
    except FileExistsError:
        try:
            descriptor = os.open("cache.lock", flags, dir_fd=cache_descriptor)
        except OSError as error:
            fail(f"cannot safely open existing cache.lock: {error}")
    except OSError as error:
        fail(f"cannot safely create cache.lock: {error}")
    validate_lock(descriptor, cache_descriptor)
    return descriptor


def lock_and_exec(arguments: list[str]) -> None:
    if len(arguments) < 4:
        fail("lock-and-exec requires cache root, input hash, and command")
    root_path, cache_hash, command, *command_arguments = arguments
    descriptors, cache_descriptor = open_cache_tree(root_path, cache_hash, create=True)
    lock_descriptor = create_and_open_lock(cache_descriptor)
    fcntl.flock(lock_descriptor, fcntl.LOCK_EX)
    validate_lock(lock_descriptor, cache_descriptor)
    # Revalidate every directory after acquiring the lock and immediately
    # before exec. The open descriptors also pin their exact inodes.
    for descriptor in descriptors:
        validate_dir_stat(os.fstat(descriptor), "persistent cache component")
    os.set_inheritable(lock_descriptor, True)
    environment = os.environ.copy()
    environment["GHELPER_CACHE_LOCK_FD"] = str(lock_descriptor)
    os.execve(command, [command, "--internal-cache-lock", str(lock_descriptor), *command_arguments], environment)


def validate_held(arguments: list[str]) -> None:
    if len(arguments) != 3:
        fail("validate-held requires cache root, input hash, and descriptor")
    root_path, cache_hash, descriptor_text = arguments
    try:
        lock_descriptor = int(descriptor_text)
    except ValueError:
        fail("invalid inherited cache-lock descriptor")
    descriptors, cache_descriptor = open_cache_tree(root_path, cache_hash, create=False)
    validate_lock(lock_descriptor, cache_descriptor)
    try:
        fcntl.flock(lock_descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as error:
        fail(f"inherited cache.lock is not held: {error}")
    for descriptor in descriptors:
        validate_dir_stat(os.fstat(descriptor), "persistent cache component")


def validate_root(arguments: list[str]) -> None:
    if len(arguments) != 1:
        fail("validate-root requires the persistent cache root")
    descriptor = open_root(arguments[0])
    os.close(descriptor)


def main() -> None:
    if len(sys.argv) < 2:
        fail("missing cache-lock operation")
    operation, *arguments = sys.argv[1:]
    if operation == "lock-and-exec":
        lock_and_exec(arguments)
    elif operation == "validate-held":
        validate_held(arguments)
    elif operation == "validate-root":
        validate_root(arguments)
    else:
        fail("unknown cache-lock operation")


if __name__ == "__main__":
    main()
