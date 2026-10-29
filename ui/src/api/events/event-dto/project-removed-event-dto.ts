import { isRecord } from "../../transport/envelopes";

/** A project was deleted: remove it from the list; an open screen of the project closes. */
export interface ProjectRemovedEventDto {
    kind: "event";
    event: "project.removed";
    data: { projectId: string };
}

export function isProjectRemovedDto(value: unknown): value is ProjectRemovedEventDto["data"] {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0;
}
