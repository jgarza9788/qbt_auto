"""
rapid string matching library
"""

from __future__ import annotations


# start delvewheel patch
def _delvewheel_patch_1_13_0():
    import os
    if os.path.isdir(libs_dir := os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir, 'rapidfuzz.libs'))):
        os.add_dll_directory(libs_dir)


_delvewheel_patch_1_13_0()
del _delvewheel_patch_1_13_0
# end delvewheel patch

__author__: str = "Max Bachmann"
__license__: str = "MIT"
__version__: str = "3.14.6"

from rapidfuzz import distance, fuzz, process, utils

__all__ = ["distance", "fuzz", "get_include", "process", "utils"]


def get_include() -> str:
    """
    Return the directory that contains the RapidFuzz \\*.h header files.
    Extension modules that need to compile against RapidFuzz should use this
    function to locate the appropriate include directory.
    Notes
    -----
    When using ``distutils``, for example in ``setup.py``.
    ::
        import rapidfuzz
        ...
        Extension('extension_name', ...
                include_dirs=[rapidfuzz.get_include()])
        ...
    """
    from pathlib import Path

    return str(Path(__file__).parent)
