from typing import Tuple, Set, Iterable, List


class BarChartFunctions:
    @overload
    def GetNodeInput(labels: List, values: List, colors: List) -> Dictionary: ...
    @overload
    def GetNodeInput(labels: List, values: List, colors: List) -> Dictionary: ...
    def GetDefaultNodeInput() -> Dictionary: ...


class BasicLineChartFunctions:
    def GetNodeInput(titles: List, values: List, colors: List) -> Dictionary: ...


class HeatSeriesFunctions:
    def GetNodeInput(xLabels: List, yLabels: List, values: List, colors: List) -> Dictionary: ...


class PieChartFunctions:
    def GetNodeInput(labels: List, values: List, colors: List) -> Dictionary: ...


class ScatterPlotFunctions:
    def GetNodeInput(titles: List, xValues: List, yValues: List, colors: List) -> Dictionary: ...


class XYLineChartFunctions:
    def GetNodeInput(titles: List, xValues: List, yValues: List, colors: List) -> Dictionary: ...
