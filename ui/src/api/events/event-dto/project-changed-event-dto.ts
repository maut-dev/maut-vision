import type { ProjectResponseDto } from "../../projects/response-dto/project-response-dto";

export interface ProjectChangedEventDto {
    kind: "event";
    event: "project.changed";
    data: ProjectResponseDto;
}
