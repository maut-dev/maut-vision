import type { JobResponseDto } from "../../jobs/response-dto/job-response-dto";

export interface JobChangedEventDto {
    kind: "event";
    event: "job.changed";
    data: JobResponseDto;
}
