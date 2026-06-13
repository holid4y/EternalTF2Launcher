from collections.abc import Callable
from typing import Any


def all_predicates(*preds: Callable[[Any], bool]) -> Callable[[Any], bool]:
    """
    Returns a function that returns true if all functions return a truthy vlaue when run with the same arguements.

    Args:
        preds: Any number of functions.

    Returns:
        A function that lazily calls all previously provided functions with the same arguments and returns true if they all return true.

    """

    return lambda x: all(p(x) for p in preds)
