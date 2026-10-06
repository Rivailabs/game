"""Document intake (Release 3): game brief -> versioned specification with owner questions."""

from .document import IntakeError, SourceDocument, read_document
from .repository import SpecError, SpecRepository
from .spec import Question, QuestionStatus, Requirement, RequirementStatus, Specification, SpecStatus

__all__ = ["IntakeError", "Question", "QuestionStatus", "Requirement", "RequirementStatus", "SourceDocument",
           "SpecError", "SpecRepository", "SpecStatus", "Specification", "read_document"]
