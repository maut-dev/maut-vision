import { isRecord } from "../../transport/envelopes";

/** Проект удалён: убрать его из списка; открытый экран проекта закрывается. */
export interface ProjectRemovedEventDto {
    kind: "event";
    event: "project.removed";
    data: { projectId: string };
}

export function isProjectRemovedDto(value: unknown): value is ProjectRemovedEventDto["data"] {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0;
}
