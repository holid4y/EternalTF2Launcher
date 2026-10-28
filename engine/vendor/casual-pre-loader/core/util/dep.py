from collections.abc import Callable, Hashable
from functools import lru_cache
from types import FunctionType
from typing import Protocol, Self, cast, overload, runtime_checkable

type Transform[R] = Callable[[R], R]


@runtime_checkable
class HasCache[R](Protocol):
    _cache: dict[str, Transform[R]]


class Dep[R: Hashable]:
    """
    A descriptor class that computes an object given another attribute of the same type, caching the result.
    """

    def __init__(self, func: Transform[R]):
        self.func: Transform[R] = func
        self.depname: str = cast(FunctionType, func).__code__.co_varnames[-1]

    def __set_name__(self, owner, name: str) -> None:
        self.name: str = name

    @overload
    def __get__[T](self, instance: None, owner: type[T]) -> Self: ...

    @overload
    def __get__[T](self, instance: T, owner: type[T]) -> R: ...

    def __get__[T](self, instance: T | None, owner: type[T]) -> R | Self:
        if instance is None:
            return self

        try:
            return instance.__dict__[self.name]
        except KeyError:
            pass

        return self._get_cached_func(instance)(getattr(instance, self.depname))

    def __set__(self, instance, value: R) -> None:
        if value is self: # guard against dataclasses' initial set
            return

        if isinstance(instance, HasCache) and self.name in instance._cache:
            del instance._cache[self.name]

        instance.__dict__[self.name] = value

    def __delete__(self, instance) -> None:
        if self.name in instance.__dict__:
            del instance.__dict__[self.name]

        if isinstance(instance, HasCache) and self.name in instance._cache:
            del instance._cache[self.name]

    def _get_cached_func(self, instance) -> Transform[R]:
        # hacky, but works well
        if not isinstance(instance, HasCache):
            instance._cache = {}

        try:
            return instance._cache[self.name]
        except KeyError:
            pass

        func = instance._cache[self.name] = lru_cache(maxsize=1)(self.func)
        return func
