from typing import Tuple, Set, Iterable, List


class GraphLinterRule(LinterRule):


class LinterRule:
    @property
    def Id(self) -> str: ...
    @property
    def SeverityCode(self) -> SeverityCodesEnum: ...
    @property
    def Description(self) -> str: ...
    @property
    def CallToAction(self) -> str: ...
    @property
    def EvaluationTriggerEvents(self) -> List: ...


class NodeLinterRule(LinterRule):
