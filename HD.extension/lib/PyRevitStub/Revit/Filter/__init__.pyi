from typing import Tuple, Set, Iterable, List


class FilterRule:
    def ByRuleType(type: str, value: Object, parameter: Parameter) -> FilterRule: ...


class OverrideGraphicSettings:
    def ByProperties(cutFillColor: Color, projectionFillColor: Color, cutLineColor: Color, projectionLineColor: Color, cutFillPattern: FillPatternElement, projectionFillPattern: FillPatternElement, cutLinePattern: LinePatternElement, projectionLinePattern: LinePatternElement, cutLineWeight: int, projectionLineWeight: int, transparency: int, detailLevel: str, halftone: bool) -> OverrideGraphicSettings: ...


class ParameterFilterElement(Element):
    @property
    def InternalElement(self) -> Element: ...
    def ByRules(name: str, categories: Iterable[Category], rules: Iterable[FilterRule]) -> ParameterFilterElement: ...


class RuleType:
    BeginsWith = 0
    Contains = 1
    EndsWith = 2
    Equals = 3
    Greater = 4
    Less = 5
    GreaterOrEqual = 6
    LessOrEqual = 7
    NotBeginsWith = 8
    NotContains = 9
    NotEndsWith = 10
    NotEquals = 11
