export interface paths {
    "/api/v1/teams": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CatalogTeam"][];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/teams/{team}/types": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    /** @description Use actual meta.<key> names for exact AND metadata filters on workflow status counts. */
                    "meta.*"?: string;
                    /** @description Apply the visible team's stored default metadata filters; explicit meta.<key> values override them. */
                    defaults?: boolean;
                };
                header?: never;
                path: {
                    team: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CatalogType"][];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/workflows": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    team?: string;
                    type?: string;
                    cursor?: string;
                    limit?: number;
                    /** @description Repeat with actual meta.<key> names for exact AND metadata equality filters (at most 32). */
                    "meta.*"?: string;
                    /** @description Apply the visible team's stored metadata defaults. Requires team; explicit meta.<key> values override defaults. */
                    defaults?: boolean;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CatalogWorkflowPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Read-only task plan from one immutable version UUID, never the active version. Workflow identity is the three exact team/type/workflow segments. Includes declared actions, dependency order, effective placement, retries and timeouts. Excludes variable values/defaults, input bindings, command arguments/environment, HTTP headers/bodies and storage capabilities. Legacy tasks without action metadata explicitly have an unknown declaration. */
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    team: string;
                    type: string;
                    workflow: string;
                    version: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WorkflowVersionPlan"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/registrations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/upload-grants": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/uploads/{grant}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    grant: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/log-upload-grants/{capability}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    capability: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/log-uploads/{grant}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    grant: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/abort": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["AbortRunRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AbortRunResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Payload Too Large */
                413: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unsupported Media Type */
                415: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Safe durable run summaries, newest UUIDv7 first. All filters combine with AND. Metadata matches the run's copied metadata, not its current workflow. Deleted workflows retain history. Fresh Viewer grants filter every page; cursors are bound to filters and visible type scopes. Includes only copied public metadata; no variable values, snapshots or storage capabilities. */
        get: {
            parameters: {
                query?: {
                    /** @description Exact registered team; omitted lists only currently visible scopes. */
                    team?: string;
                    /** @description Exact registered type name; requires team. */
                    type?: string;
                    /** @description Exact team/type/workflow identity; unknown or ungranted resource is forbidden. */
                    workflowId?: string;
                    /** @description Canonical nonempty lowercase UUID; unknown, ambiguous or ungranted series is forbidden. */
                    seriesId?: string;
                    /** @description One exact case-sensitive RunStatus name. */
                    status?: "Queued" | "Running" | "Aborting" | "Succeeded" | "Failed" | "Aborted" | "TimedOut";
                    /** @description Inclusive created_at lower bound; explicit RFC3339 offset, microsecond precision. */
                    from?: string;
                    /** @description Exclusive created_at upper bound; explicit RFC3339 offset, microsecond precision. */
                    to?: string;
                    /** @description Canonical integer 1–100, default 50. Keep all filters and limit unchanged on subsequent pages. */
                    limit?: number;
                    /** @description Opaque canonical nextCursor from this filter/scope combination; no offset paging. */
                    cursor?: string;
                    /** @description Dynamic query-name prefix, not a literal key: meta.environment=prod. Up to 32 exact string equality filters, ANDed; each suffix 1–128 characters, value ≤1024. Copied run metadata.values only. */
                    "meta.*"?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunQueryPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Committed live resync after one canonical nonnegative Int64 afterSeq. Returns at most 5000 ordered events and their authoritative lastSeq; 409 requires a new run snapshot. */
        get: {
            parameters: {
                query: {
                    /** @description Canonical nonnegative Int64 decimal (0–9223372036854775807); preserve exact digits. */
                    afterSeq: string;
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunEventsPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Authoritative committed run state restored from its materialized snapshot and event tail. An accepted pending trigger exposes its queued initial state at lastSeq 0 before materialization. lastSeq matches the returned graph state. No variables, payloads, outputs, or storage capabilities. */
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunSnapshotResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Durable workflow/series history: runs plus retained skipped/missed fire facts, including deleted resources. Run filters share /runs semantics; from/to bound the row timestamp. status, abortedBy or meta.* exclude fire-only rows. sort=createdAtDesc (default) or createdAtAsc; stable timestamp/UUID/kind keyset, bound to all filters, limit and fresh Viewer scopes. outcome=Run|Skipped|Missed; abortedBy is an exact principal UUID from the first committed abort event, not inferred from status. Run versionId is the executed workflow version, NOT code-schedule config. fire.scheduleConfigVersionId is the immutable config at claim, null for user/legacy fires. currentScheduleId is the authoritative non-deleted schedule linked by run_series for an explicit seriesId; null after schedule deletion, for run-only legacy series or without a series filter. Variables project only set schema-backed names/types; all values/defaults/secret references omitted, Secret entries marked redacted. Unknown legacy schemas produce []. Copied metadata is public author-declared metadata. Duration is endedAt-startedAt in milliseconds, null unless both exist. No storage capabilities. */
        get: {
            parameters: {
                query?: {
                    workflowId?: string;
                    seriesId?: string;
                    team?: string;
                    type?: string;
                    status?: "Queued" | "Running" | "Aborting" | "Succeeded" | "Failed" | "Aborted" | "TimedOut";
                    from?: string;
                    to?: string;
                    "meta.*"?: string;
                    limit?: number;
                    cursor?: string;
                    sort?: "createdAtDesc" | "createdAtAsc";
                    abortedBy?: string;
                    outcome?: "Run" | "Skipped" | "Missed";
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunHistoryPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/workflows/{team}/{type}/{workflow}/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Durable workflow/series history: runs plus retained skipped/missed fire facts, including deleted resources. Run filters share /runs semantics; from/to bound the row timestamp. status, abortedBy or meta.* exclude fire-only rows. sort=createdAtDesc (default) or createdAtAsc; stable timestamp/UUID/kind keyset, bound to all filters, limit and fresh Viewer scopes. outcome=Run|Skipped|Missed; abortedBy is an exact principal UUID from the first committed abort event, not inferred from status. Run versionId is the executed workflow version, NOT code-schedule config. fire.scheduleConfigVersionId is the immutable config at claim, null for user/legacy fires. currentScheduleId is the authoritative non-deleted schedule linked by run_series for an explicit seriesId; null after schedule deletion, for run-only legacy series or without a series filter. Variables project only set schema-backed names/types; all values/defaults/secret references omitted, Secret entries marked redacted. Unknown legacy schemas produce []. Copied metadata is public author-declared metadata. Duration is endedAt-startedAt in milliseconds, null unless both exist. No storage capabilities. */
        get: {
            parameters: {
                query?: {
                    workflowId?: string;
                    seriesId?: string;
                    team?: string;
                    type?: string;
                    status?: "Queued" | "Running" | "Aborting" | "Succeeded" | "Failed" | "Aborted" | "TimedOut";
                    from?: string;
                    to?: string;
                    "meta.*"?: string;
                    limit?: number;
                    cursor?: string;
                    sort?: "createdAtDesc" | "createdAtAsc";
                    abortedBy?: string;
                    outcome?: "Run" | "Skipped" | "Missed";
                };
                header?: never;
                path: {
                    team: string;
                    type: string;
                    workflow: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunHistoryPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/series/{id}/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Durable workflow/series history: runs plus retained skipped/missed fire facts, including deleted resources. Run filters share /runs semantics; from/to bound the row timestamp. status, abortedBy or meta.* exclude fire-only rows. sort=createdAtDesc (default) or createdAtAsc; stable timestamp/UUID/kind keyset, bound to all filters, limit and fresh Viewer scopes. outcome=Run|Skipped|Missed; abortedBy is an exact principal UUID from the first committed abort event, not inferred from status. Run versionId is the executed workflow version, NOT code-schedule config. fire.scheduleConfigVersionId is the immutable config at claim, null for user/legacy fires. currentScheduleId is the authoritative non-deleted schedule linked by run_series for an explicit seriesId; null after schedule deletion, for run-only legacy series or without a series filter. Variables project only set schema-backed names/types; all values/defaults/secret references omitted, Secret entries marked redacted. Unknown legacy schemas produce []. Copied metadata is public author-declared metadata. Duration is endedAt-startedAt in milliseconds, null unless both exist. No storage capabilities. */
        get: {
            parameters: {
                query?: {
                    workflowId?: string;
                    seriesId?: string;
                    team?: string;
                    type?: string;
                    status?: "Queued" | "Running" | "Aborting" | "Succeeded" | "Failed" | "Aborted" | "TimedOut";
                    from?: string;
                    to?: string;
                    "meta.*"?: string;
                    limit?: number;
                    cursor?: string;
                    sort?: "createdAtDesc" | "createdAtAsc";
                    abortedBy?: string;
                    outcome?: "Run" | "Skipped" | "Missed";
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunHistoryPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/metrics": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Committed run, lifecycle-stage, node, task and attempt timings, in milliseconds. Stages use their reserved keys; nodes excludes stages. Active durations are recorded values, not live clocks. Task duration sums execution only; retries count redispatches. Failure categories preserve known producer metadata; absent legacy/unknown categories are null. Never-started/skipped/reused work has no fabricated attempts. Fresh Run Viewer authorization; no query parameters, variables, errors, outputs or storage capabilities. */
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RunMetricsResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Internal Server Error */
                500: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/tasks/{task}/{attempt}/logs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Viewer task-attempt logs. task is the URI-escaped stored node/task key. offset is zero-based original record offset; length is 1–1000 (default 200). Alternatively search (regex, 2048 characters, 10 s total, 100 ms per match, 1000 matches, cursor), or download=true (NDJSON). */
        get: {
            parameters: {
                query?: {
                    offset?: string;
                    length?: string;
                    search?: string;
                    download?: string;
                    cursor?: string;
                };
                header?: never;
                path: {
                    id: string;
                    task: string;
                    attempt: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TaskLogsPage"];
                        "application/x-ndjson": string;
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Bad Gateway */
                502: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Gateway Timeout */
                504: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/schedules/{id}/run-now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ScheduleRunNowResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Payload Too Large */
                413: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unsupported Media Type */
                415: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/series/{id}/fires": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    limit?: number | string;
                    cursor?: string;
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SeriesFiresPage"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/schedules/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["UserScheduleResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/healthz": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/healthz/leader": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AbortRunRequest: {
            reason: string;
            source?: components["schemas"]["AbortSource"];
        };
        AbortRunResponse: {
            by: string;
            byName: string;
            /** Format: date-time */
            at: string;
            reason: string;
            source: components["schemas"]["AbortSource"];
        };
        /** @enum {unknown} */
        AbortSource: "UI" | "API" | "CLI" | "KillSwitch" | "Local";
        /** @enum {unknown} */
        AffinityKind: "None" | "Node" | "Run" | "SameMachineAs";
        /** @enum {unknown} */
        BackoffKind: "Fixed" | "Exponential";
        BackoffPolicy: {
            kind: components["schemas"]["BackoffKind"];
            delay: string;
            maxDelay: string;
        };
        CatalogLatestRun: {
            /** Format: uuid */
            id: string;
            status: string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            startedAt: null | string;
            /** Format: date-time */
            endedAt: null | string;
        };
        CatalogMetadataField: {
            key: string;
            required: boolean;
            allowedValues: string[];
        };
        CatalogMetadataSchema: {
            keys: components["schemas"]["CatalogMetadataField"][];
            allowFreeForm: boolean;
        };
        CatalogPresentation: {
            typeOrder: string[];
            groupingKeys: string[];
            listColumns: string[];
            defaultFilters: {
                [key: string]: string;
            };
            labels: {
                [key: string]: string;
            };
        };
        CatalogRegistryType: {
            team: string;
            name: string;
            description: string;
            metadataSchema: components["schemas"]["CatalogMetadataSchema"];
            roleBindings: components["schemas"]["RegistryRoleBinding"][];
        };
        CatalogTeam: {
            team: string;
            types: components["schemas"]["CatalogRegistryType"][];
            $schema: null | string;
            roleBindings: components["schemas"]["RegistryRoleBinding"][];
            presentation: null | components["schemas"]["CatalogPresentation"];
        };
        CatalogType: {
            team: string;
            name: string;
            description: string;
            metadataSchema: components["schemas"]["CatalogMetadataSchema"];
            latestStatusCounts: {
                [key: string]: number | string;
            };
        };
        CatalogVersion: {
            /** Format: uuid */
            id: string;
            semVer: string;
        };
        CatalogWorkflow: {
            id: string;
            team: string;
            type: string;
            name: string;
            activeVersion: null | components["schemas"]["CatalogVersion"];
            latestVersion: components["schemas"]["CatalogVersion"];
            metadata: components["schemas"]["WorkflowMetadata"];
            latestRun: null | components["schemas"]["CatalogLatestRun"];
        };
        CatalogWorkflowPage: {
            items: components["schemas"]["CatalogWorkflow"][];
            nextCursor: null | string;
        };
        /**
         * @default OnSuccess
         * @enum {unknown}
         */
        DependencyCondition: "OnSuccess" | "OnFailure" | "Always";
        DependencyDefinition: {
            node: string;
            when?: components["schemas"]["DependencyCondition"];
        };
        /** @enum {unknown} */
        FailureCategory: "TaskError" | "Timeout" | "AgentLost" | "PlatformError" | "ValidationError" | "Aborted" | "ResourceLimit" | "Placement";
        /** @enum {unknown} */
        FailurePolicy: "Hard" | "Soft";
        LabelSelector: {
            key: string;
            value?: components["schemas"]["SelectorValue"];
        };
        ListDelta: {
            scope: string;
            id: string;
            items: {
                runId: string;
                workflowId: string;
                versionId: string;
                seriesId: null | string;
                /** Format: int32 */
                status: number | string;
                /** Format: int64 */
                lastSeq: number | string;
                createdAt: string;
                endedAt: null | string;
            }[];
        };
        LiveAbort: null | {
            by: string;
            byName: string;
            at: string;
            reason: string;
            source: string;
        };
        LiveEvent: {
            /** Format: int64 */
            seq: number | string;
            at: string;
            /** Format: int32 */
            entity: number | string;
            key: null | string;
            /** Format: int32 */
            state: number | string;
            abort: null | components["schemas"]["LiveAbort"];
        };
        LiveResync: {
            scope: string;
            id: string;
        };
        LiveRunSummary: {
            runId: string;
            workflowId: string;
            versionId: string;
            seriesId: null | string;
            /** Format: int32 */
            status: number | string;
            /** Format: int64 */
            lastSeq: number | string;
            createdAt: string;
            endedAt: null | string;
        };
        LiveSubscriptionProblem: {
            /** Format: int32 */
            status: number | string;
            title: string;
            detail: string;
            reason?: null | string;
        };
        LogFrame: {
            /** Format: date-time */
            at: string;
            stream: string;
            level: string;
            text: string;
        };
        MetricAttempt: {
            /** Format: int32 */
            attempt: number | string;
            /** Format: date-time */
            queuedAt: string;
            /** Format: date-time */
            dispatchedAt: string;
            /** Format: date-time */
            startedAt: null | string;
            /** Format: date-time */
            endedAt: null | string;
            /** Format: double */
            queueWaitMs: number | string;
            /** Format: double */
            durationMs: number | string;
            terminalStatus: null | string;
            failureCategory: null | string;
        };
        MetricEntity: {
            status: string;
            /** Format: date-time */
            queuedAt: null | string;
            /** Format: date-time */
            startedAt: null | string;
            /** Format: date-time */
            endedAt: null | string;
            /** Format: double */
            queueWaitMs: number | string;
            /** Format: double */
            durationMs: number | string;
            /** Format: int32 */
            retryCount: number | string;
            terminalStatus: null | string;
            failureCategory: null | string;
            attempts: components["schemas"]["MetricAttempt"][];
        };
        /** @enum {unknown} */
        NodeExecutionMode: "Series" | "Parallel";
        PlacementAffinity: {
            kind: components["schemas"]["AffinityKind"];
            nodeKey?: null | string;
        };
        ProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number | string;
            detail?: null | string;
            instance?: null | string;
        };
        /** @enum {unknown} */
        RegistryRole: "Viewer" | "Operator" | "Author" | "Admin";
        RegistryRoleBinding: {
            role: components["schemas"]["RegistryRole"];
            /** Format: uuid */
            groupId: string;
        };
        RunDelta: {
            runId: string;
            events: {
                /** Format: int64 */
                seq: number | string;
                at: string;
                /** Format: int32 */
                entity: number | string;
                key: null | string;
                /** Format: int32 */
                state: number | string;
                abort: null | {
                    by: string;
                    byName: string;
                    at: string;
                    reason: string;
                    source: string;
                };
            }[];
        };
        RunEventsPage: {
            /** Format: uuid */
            runId: string;
            /** Format: int64 */
            lastSeq: number | string;
            events: components["schemas"]["LiveEvent"][];
        };
        RunHistoryFire: {
            /** Format: uuid */
            scheduleId: string;
            /** Format: date-time */
            fireAt: string;
            outcome: string;
            reason: null | string;
            /** Format: uuid */
            scheduleConfigVersionId: null | string;
        };
        RunHistoryPage: {
            items: components["schemas"]["RunHistoryRow"][];
            nextCursor: null | string;
            /** Format: uuid */
            currentScheduleId: null | string;
        };
        RunHistoryRow: {
            kind: string;
            /** Format: date-time */
            at: string;
            workflowId: string;
            /** Format: uuid */
            seriesId: null | string;
            /** Format: uuid */
            runId: null | string;
            /** Format: uuid */
            versionId: null | string;
            status: null | string;
            /** Format: date-time */
            startedAt: null | string;
            /** Format: date-time */
            endedAt: null | string;
            /** Format: double */
            durationMs: null | number | string;
            trigger: null | string;
            metadata: null | components["schemas"]["RunMetadata"];
            abort: null | components["schemas"]["LiveAbort"];
            variables: components["schemas"]["RunHistoryVariable"][];
            fire: null | components["schemas"]["RunHistoryFire"];
        };
        RunHistoryVariable: {
            key: string;
            type: string;
            redacted: boolean;
        };
        RunMetadata: {
            type: string;
            values: {
                [key: string]: string;
            };
        };
        RunMetricsResponse: {
            /** Format: uuid */
            runId: string;
            /** Format: int64 */
            lastSeq: number | string;
            status: string;
            run: components["schemas"]["MetricEntity"];
            stages: {
                [key: string]: components["schemas"]["MetricEntity"];
            };
            nodes: {
                [key: string]: components["schemas"]["MetricEntity"];
            };
            tasks: {
                [key: string]: components["schemas"]["MetricEntity"];
            };
        };
        RunQueryPage: {
            items: components["schemas"]["RunQueryRow"][];
            nextCursor: null | string;
        };
        RunQueryRow: {
            /** Format: uuid */
            id: string;
            workflowId: string;
            /** Format: uuid */
            versionId: string;
            /** Format: uuid */
            seriesId: null | string;
            team: string;
            type: string;
            status: string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            startedAt: null | string;
            /** Format: date-time */
            endedAt: null | string;
            metadata: null | components["schemas"]["RunMetadata"];
        };
        RunSnapshotResponse: {
            /** Format: uuid */
            runId: string;
            workflowId: string;
            /** Format: uuid */
            versionId: string;
            /** Format: uuid */
            seriesId: null | string;
            /** Format: int32 */
            status: number | string;
            /** Format: int64 */
            lastSeq: number | string;
            nodes: {
                [key: string]: number | string;
            };
            tasks: {
                [key: string]: number | string;
            };
            abort: null | components["schemas"]["LiveAbort"];
            endedAt: null | string;
        };
        /** @enum {unknown} */
        ScheduleCatchUpPolicy: "None" | "Latest";
        /** @enum {unknown} */
        ScheduleOverlapPolicy: "Skip" | "Queue" | "Concurrent";
        ScheduleRunNowResponse: {
            /** Format: uuid */
            runId: string;
        };
        SelectorValue: {
            literal?: null | string;
            variable?: null | string;
        };
        SeriesFireResponse: {
            /** Format: date-time */
            fireAt: string;
            outcome: string;
            /** Format: uuid */
            runId: null | string;
            reason: null | string;
        };
        SeriesFiresPage: {
            /** Format: uuid */
            seriesId: string;
            items: components["schemas"]["SeriesFireResponse"][];
            nextCursor: null | string;
        };
        TaskAction: {
            kind: components["schemas"]["TaskActionKind"];
            declaration: string;
        };
        /** @enum {unknown} */
        TaskActionKind: "SdkFunction" | "Command" | "Http" | "HttpOperation";
        TaskLogMatch: {
            /** Format: int64 */
            line: number | string;
            /** Format: int32 */
            index: number | string;
            /** Format: int32 */
            length: number | string;
            frame: components["schemas"]["LogFrame"];
        };
        TaskLogRecord: {
            /** Format: int64 */
            line: number | string;
            frame: components["schemas"]["LogFrame"];
        };
        TaskLogsPage: {
            /** Format: int64 */
            totalLines: number | string;
            /** Format: int64 */
            droppedLines: number | string;
            truncated: boolean;
            captureInterrupted: boolean;
            truncationMarker: null | string;
            records: components["schemas"]["TaskLogRecord"][];
            matches: components["schemas"]["TaskLogMatch"][];
            nextCursor: null | string;
        };
        TaskPlanExecutionPolicy: {
            attemptTimeout: string;
            retry: components["schemas"]["TaskPlanRetry"];
            autoRetry: boolean;
        };
        TaskPlanNode: {
            key: string;
            dependencies: components["schemas"]["DependencyDefinition"][];
            mode: components["schemas"]["NodeExecutionMode"];
            /** Format: int32 */
            maxParallel: null | number | string;
            failure: components["schemas"]["FailurePolicy"];
            timeout: null | string;
            placement: null | components["schemas"]["TaskPlanPlacement"];
            tasks: components["schemas"]["TaskPlanTask"][];
        };
        TaskPlanPlacement: {
            selector: null | components["schemas"]["TaskPlanSelector"];
            affinity: null | components["schemas"]["PlacementAffinity"];
        };
        TaskPlanRetry: {
            /** Format: int32 */
            retries: number | string;
            backoff: components["schemas"]["BackoffPolicy"];
            on: components["schemas"]["FailureCategory"][];
        };
        TaskPlanSelector: {
            os: null | components["schemas"]["SelectorValue"];
            pool: null | components["schemas"]["SelectorValue"];
            labels: components["schemas"]["LabelSelector"][];
            machines: components["schemas"]["SelectorValue"][];
        };
        TaskPlanTask: {
            key: string;
            action: null | components["schemas"]["TaskAction"];
            secretReference: null | string;
            requestTimeout: null | string;
            failure: components["schemas"]["FailurePolicy"];
            policy: components["schemas"]["TaskPlanExecutionPolicy"];
            placement: null | components["schemas"]["TaskPlanPlacement"];
        };
        UserScheduleResponse: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            seriesId: string;
            workflowId: string;
            name: string;
            origin: string;
            timezone: string;
            cron: null | string;
            interval: null | string;
            versionPin: null | string;
            overlap: components["schemas"]["ScheduleOverlapPolicy"];
            catchUp: components["schemas"]["ScheduleCatchUpPolicy"];
            variables: Record<string, unknown>;
            paused: boolean;
            /** Format: int32 */
            rowVersion: number | string;
            /** Format: date-time */
            nextFireAt: null | string;
            sourcePath?: null | string;
            gitCommit?: null | string;
        };
        WorkflowMetadata: {
            type: string;
            values: {
                [key: string]: string;
            };
        };
        WorkflowVersionPlan: {
            /** Format: uuid */
            versionId: string;
            workflowId: string;
            version: string;
            runTimeout: string;
            stages: string[];
            placement: null | components["schemas"]["TaskPlanPlacement"];
            nodes: components["schemas"]["TaskPlanNode"][];
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export type operations = Record<string, never>;
