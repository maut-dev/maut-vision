import type { ProjectResponseDto } from "../../projects/response-dto/project-response-dto";

export interface ProjectOpenedEventDto {
    kind: "event";
    event: "project.opened";
    data: ProjectResponseDto;
}
