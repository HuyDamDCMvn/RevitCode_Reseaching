from typing import Tuple, Set, Iterable, List


class ElementCurveReference(ElementGeometryReference):
    def ByCurve(curve: Curve) -> ElementCurveReference: ...


class ElementFaceReference(ElementGeometryReference):
    def BySurface(surface: Surface) -> ElementFaceReference: ...


class ElementGeometryReference:


class ElementPlaneReference(ElementGeometryReference):
