import type { ProjectImportResponseDto } from "../../projects/response-dto/project-lifecycle-response-dto";

/** Снимок импорта проекта (ход прореживается, переходы — сразу); применять, только если sequence больше известного. */
export interface ProjectImportChangedEventDto {
    kind: "event";
    event: "projectImport.changed";
    data: ProjectImportResponseDto;
}
