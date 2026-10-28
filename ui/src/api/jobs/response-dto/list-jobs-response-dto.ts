import { isJobResponseDto, type JobResponseDto } from "./job-response-dto";

export type ListJobsResponseDto = JobResponseDto[];

export function isListJobsResponseDto(value: unknown): value is ListJobsResponseDto {
    return Array.isArray(value) && value.every(isJobResponseDto);
}
