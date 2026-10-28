import type { BaseModelResponseDto } from "../../base-models/response-dto/base-model-response-dto";

export interface BaseModelChangedEventDto {
    kind: "event";
    event: "baseModel.changed";
    data: BaseModelResponseDto;
}
