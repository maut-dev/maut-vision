# API and transport

The domain API is separated from message delivery. C# and TypeScript use matching DTOs; Transport does not depend on business entities.

```text
Desktop/Controllers/<area>/
    Controller.cs
    RequestDto/
    ResponseDto/

ui/src/api/<area>/
    request-dto/
    response-dto/
    <area>-api.ts
ui/src/api/transport/
```

React calls a typed API. A controller receives a typed request, calls Core and returns a DTO. Transport is responsible for JSON, the
request identifier, routing and delivery errors. DTO changes are checked on both sides at once, including null, dates and enums.

The UI and Desktop ship together, so there is no `protocolVersion`. Incoming messages are still validated. A frontend timeout or a
cancelled wait does not by itself cancel a Core operation.

A response is awaited for 10 seconds. A request that waits for the user in a native window (file selection) passes `timeoutMs: null`;
selected files, folders or videos stay in the host behind a selection token, and paths never reach the UI.

A request is for short operations; long ones are [jobs](../Core/jobs.md#long-operations). Messages are size-limited, so images, weights
and files never travel in JSON messages: a DTO contains a [resource channel](resources.md) URL. Errors follow the
[error model](../Core/errors.md).
