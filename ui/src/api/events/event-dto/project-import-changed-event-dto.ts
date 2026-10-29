import type { ProjectImportResponseDto } from "../../projects/response-dto/project-lifecycle-response-dto";

/** A snapshot of a project import (progress is thinned out, transitions come at once); apply it only when sequence is greater than the known one. */
export interface ProjectImportChangedEventDto {
    kind: "event";
    event: "projectImport.changed";
    data: ProjectImportResponseDto;
}
