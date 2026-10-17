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
    "/api/v1/workflows/{team}/{type}/{workflow}/diff": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Fresh Viewer-authorized comparison of two exact immutable version UUIDs in the same workflow, including inactive and soft-deleted versions. Keyed node/variable changes, workflow policies and settings include before/after manifest declarations. Source contains exact retained workflow.cs texts for a client diff editor. Includes authored literal configuration/source, but never resolves secrets or returns storage capabilities. No active-version fallback. Both source archives are SHA-256/length verified and bounded to 1 MiB of UTF-8 per script. */
        get: {
            parameters: {
                query: {
                    from: string;
                    to: string;
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
                        "application/json": components["schemas"]["WorkflowVersionDiff"];
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
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Download a ZIP scaffold from one exact immutable version UUID, including historical and soft-deleted sources. Body: newName is a workflow leaf, targetType is an exact team/type scope. Fresh Viewer(source type) and Author(target type) permissions are required. Existing targets (including deleted) conflict. No registration, activation or database mutation. Only literal Workflow.Define/Type fluent declarations are supported; unsupported source returns 400. Authored tasks and directives are preserved, version starts at 1.0.0, Metadata("ClonedFrom") records source@UUID. Schedules start empty and tests validate identity and lineage. Source is bounded, SHA-256/length verified and parsed only, never compiled/executed by this endpoint. Target registry schema requirements must be reviewed before publishing. */
        post: {
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
            requestBody: {
                content: {
                    "application/json": {
                        /** @description New workflow leaf name. */
                        newName: string;
                        /** @description Exact team/type scope. */
                        targetType: string;
                    };
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/zip": string;
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
                /** @description Unsupported Media Type */
                415: {
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
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Fresh owning-type Admin authorization activates one exact immutable version UUID. No body or query parameters. Atomically updates only the active workflow-version pointer, audits before/after UUIDs and notifies registry listeners. Latest registration baseline, pinned runs, schedule configs and fire cursors remain unchanged. Incompatible unpinned schedule variables conflict; pinned schedules keep their selected versions. Repeating the active UUID is a no-op. */
        post: {
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
                        "application/json": components["schemas"]["WorkflowVersionActivation"];
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
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/workflows/{team}/{type}/{workflow}/versions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Fresh Viewer-authorized registration history, including inactive and soft-deleted workflows. Newest registration timestamp first, then descending immutable UUID for ties. Cursor is bound to exact workflow identity and page limit; recheck authorization on every page. Lists SemVer, Git author/commit, registering identity, change note, SDK version and registration time, plus active/latest version pointers from the same database snapshot. No source, variable values or storage capabilities. */
        get: {
            parameters: {
                query?: {
                    limit?: number;
                    cursor?: string;
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
                        "application/json": components["schemas"]["WorkflowVersionPage"];
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
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Read-only task plan from one immutable version UUID, never the active version. Workflow identity is the three exact team/type/workflow segments. Includes declared actions, dependency order, effective placement, retries and timeouts. Excludes variable values/defaults, input bindings, command arguments/environment, HTTP headers/bodies and storage capabilities. Legacy tasks without action metadata explicitly have an unknown declaration. Registration includes immutable Git author/commit, registering identity, change note, SDK version and timestamp. */
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
    "/api/v1/workflows/{team}/{type}/{workflow}/variables": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Trigger variable schema and literal defaults for an Operator or Admin on the exact type scope. Optional version is one canonical UUID belonging to this undeleted workflow; omitted selects its current active version. No other query parameters are supported. Secrets contain names only; no secret resolution or task configuration is exposed. */
        get: {
            parameters: {
                query?: {
                    version?: string;
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
                        "application/json": components["schemas"]["WorkflowVariablesResponse"];
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
    "/api/v1/report-publishes/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Attempt-scoped report upload. Requires Authorization: Bearer <report capability>, not an Entra user token. X-Wf-Report-Metadata is bounded base64url JSON metadata; the body is raw binary. */
        post: {
            parameters: {
                query?: never;
                header: {
                    "X-Wf-Report-Metadata": string;
                };
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/octet-stream": string;
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ReportReceipt"];
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
                    content?: never;
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
                    content?: never;
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
    "/api/v1/artifact-grants/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Attempt-capability bearer authentication, not an Entra user token. Reserves exact bytes against the durable 5,000,000,000-byte run budget before upload. */
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
                    "application/json": components["schemas"]["ArtifactMetadata"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ArtifactGrant"];
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
                    content?: never;
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
                    content?: never;
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
        delete: {
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
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
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
                    content?: never;
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
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/artifact-publishes/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        /** @description Raw, exact Content-Length upload against an admitted artifact grant. Requires the dispatched attempt bearer capability; returns only after verified bytes and metadata commit. */
        put: {
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
                    "application/octet-stream": string;
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ArtifactReceipt"];
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
                    content?: never;
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
                    content?: never;
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
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/artifacts": {
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
                        "application/json": components["schemas"]["ArtifactSummary"][];
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
                    content?: never;
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
    "/api/v1/artifacts/{id}/content": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Fresh run-scoped Viewer authorization. Integrity-checked streamed attachment, never inline active content. */
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
                        "application/octet-stream": string;
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
                    content?: never;
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
    "/api/v1/runs/{id}/resource-metrics": {
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
                        "application/json": components["schemas"]["ResourceAttemptSummary"][];
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
                    content?: never;
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
    "/api/v1/runs/{id}/resources": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Fresh run Viewer authorization; newest-first, integrity-verified resource samples. taskKey=node/task, attempt=1..32767, limit=1..120 (default 60), optional opaque cursor; nulls retain unavailable reasons. */
        get: {
            parameters: {
                query: {
                    /** @description Exact stored node/task key; URI-encode as a query value. */
                    taskKey: string;
                    /** @description Attempt number, 1..32767. */
                    attempt: number;
                    /** @description Page size, 1..120; default 60. */
                    limit?: number;
                    /** @description Opaque nextCursor from this attempt; expires after one hour. */
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
                        "application/json": components["schemas"]["ResourceSeriesPage"];
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
                    content?: never;
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
                    content?: never;
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
    "/api/v1/runs/{id}/rerun": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Fresh Run Operator authorization reruns a terminal run's exact pinned workflow version, effective variables and metadata. Optional fromNodes invalidates those nodes and their descendants; otherwise unsuccessful nodes rerun, or all nodes for a successful source. Reusable successful node outputs and source lineage are copied into one durable leader-fenced rerun command. Idempotency-Key replays the accepted run ID for 24 hours, scoped to actor and canonical request. */
        post: {
            parameters: {
                query?: never;
                header?: {
                    /** @description Optional retry key: one value of 1 to 200 visible ASCII characters without whitespace. */
                    "Idempotency-Key"?: string;
                };
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["RerunRunRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TriggerRunResponse"];
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
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/runs/{id}/outputs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Structured JSON outputs of one exact committed task attempt, during or after its run. Fresh Run Viewer authorization. No ancestor/source fallback; null means no recorded outputs. Task output objects are limited to 65536 UTF-8 bytes; no errors, variables or capabilities are returned. */
        get: {
            parameters: {
                query: {
                    /** @description Exact stored node/task key; URI-encode as a query value. */
                    taskKey: string;
                    /** @description Exact attempt number, 1..32767. */
                    attempt: number;
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
                        "application/json": components["schemas"]["TaskOutputsResponse"];
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
                    content?: never;
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
                    content?: never;
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
    "/api/v1/runs/{id}/reservation-queue": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Current one-based global FIFO position of each waiting node. Fresh Run Viewer authorization. Includes one matching unreleased blocking reservation and its run only when that run is also currently visible. Ended/aborted owners are excluded immediately; empty items means no live reservation wait. No selectors, variables, machine labels or capabilities. Independent of run-event sequence; poll for changes. */
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
                        "application/json": components["schemas"]["ReservationQueueResponse"];
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
                header?: {
                    /** @description Optional retry key: one value of 1 to 200 visible ASCII characters without whitespace. */
                    "Idempotency-Key"?: string;
                };
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["TriggerRunRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TriggerRunResponse"];
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
    "/api/v1/audit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Global Admin audit trail. Exact actor/targetKind/targetId filters combine with AND. from is inclusive and to exclusive (RFC3339 with offset, microsecond precision). Newest first, stable timestamp/ID keyset bound to filters and limit (1-100, default 50). IDs are decimal strings to preserve Int64 precision. Summaries are committed audit metadata, not workflow variables or storage capabilities. */
        get: {
            parameters: {
                query?: {
                    actor?: string;
                    targetKind?: string;
                    targetId?: string;
                    from?: string;
                    to?: string;
                    limit?: number;
                    cursor?: string;
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
                        "application/json": components["schemas"]["AuditPage"];
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
    "/api/v1/runs/{id}/timeline": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description One row per committed dispatched attempt, including reserved lifecycle-stage keys. Manifest task declaration order, then increasing attempt; absent manifest uses snapshot task enumeration. Timings reuse validated recorded metrics, not live clocks. Agent identity belongs to the exact durable dispatch attempt; missing legacy attribution is null. Never-started/skipped/reused work has no invented attempts. Fresh Run Viewer authorization; no query parameters, variables, errors, outputs or machine capabilities. */
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
                        "application/json": components["schemas"]["RunTimelineResponse"];
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
    "/api/v1/reports/{id}/content": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Viewer-scoped immutable report bytes, streamed and integrity checked. HTML/SVG download on raw navigation; the viewer embeds authenticated bytes in an opaque-origin sandbox. Unknown kinds download. An interrupted or corrupt stream is aborted, never a successfully completed partial response. */
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
                        "application/octet-stream": string;
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
                    content?: never;
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
    "/api/v1/runs/{id}/reports": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Fresh Run Viewer metadata only. Optional kind/tag filters are single, exact, case-sensitive, nonblank values of at most 128 UTF-8 bytes. Both filters combine with AND. Stable createdAt/id order; sourceKey is the exact historical stage/node/task key, attempt is real, createdAt is committed UTC. No payload, variables, hash, blob path or download capability. On run Resync or SignalR reconnect, refetch this index; ReportAdded is an invalidation, not a run event. */
        get: {
            parameters: {
                query?: {
                    /** @description Single exact nonblank value; at most 128 UTF-8 bytes. */
                    kind?: string;
                    /** @description Single exact nonblank value; at most 128 UTF-8 bytes. */
                    tag?: string;
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
                        "application/json": components["schemas"]["ReportIndexResponse"];
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
                    content?: never;
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
    "/api/v1/series/{id}/reports/compare": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Compare metadata for two distinct actual runs in one stored series, with fresh Viewer authorization on the series and both runs. Exact case-sensitive sourceKey and title; no normalization. No payload, variables, hash, blob path or capabilities. Missing either match is 404 (report_match_not_found); multiple matches in either run, including attempts, are 409 (report_match_ambiguous). Missing takes precedence. Neither failure identifies a side or returns metadata. */
        get: {
            parameters: {
                query: {
                    /** @description Canonical lowercase RFC UUIDv7 of a distinct actual run in this series. */
                    leftRunId: string;
                    /** @description Canonical lowercase RFC UUIDv7 of a distinct actual run in this series. */
                    rightRunId: string;
                    /** @description Single exact historical source, including stages; nonblank, at most 1025 UTF-8 bytes. */
                    sourceKey: string;
                    /** @description Single exact author title; nonblank, at most 512 UTF-8 bytes. */
                    title: string;
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
                        "application/json": components["schemas"]["ReportCompareResponse"];
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
                    content?: never;
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
    "/healthz/ready": {
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
                /** @description Service Unavailable */
                503: {
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
    "/api/v1/machines": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** @description Global Admin inventory, ordered by immutable machine ID. Optional pool, os=windows|linux, status=Healthy|Unhealthy|Draining and repeated label=key=value filters combine with AND. Stale healthy heartbeats are reported Unhealthy. Current reservation remains until physical fence settlement. */
        get: {
            parameters: {
                query?: {
                    /** @description Exact pool name. */
                    pool?: string;
                    /** @description windows or linux. */
                    os?: string;
                    /** @description Healthy, Unhealthy or Draining; includes heartbeat freshness. */
                    status?: string;
                    /** @description Repeat label=key=value for AND matching, with distinct keys; at most 100. */
                    label?: string[];
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
                        "application/json": components["schemas"]["MachineListResponse"];
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
    "/api/v1/machines/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        /** @description Global Admin only. Atomically replaces supplied labels and/or pool with a before/after audit. Omitted fields stay unchanged; empty labels clears labels. Overrides survive agent reconnects. Does not interrupt running attempts or release reservations. */
        patch: {
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
                    "application/json": components["schemas"]["MachinePatchRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
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
        ArtifactGrant: {
            /** Format: uuid */
            artifactId: string;
            /** Format: date-time */
            expiresAt: string;
            committed: boolean;
        };
        ArtifactMetadata: {
            name: string;
            contentType: string;
            /** Format: int64 */
            sizeBytes: number | string;
            sha256: string;
        };
        ArtifactReceipt: {
            /** Format: uuid */
            runId: string;
            /** Format: uuid */
            artifactId: string;
        };
        ArtifactSummary: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            runId: string;
            sourceKey: string;
            name: string;
            contentType: string;
            /** Format: int64 */
            sizeBytes: number | string;
            sha256: string;
            /** Format: date-time */
            createdAt: string;
        };
        AuditPage: {
            items: components["schemas"]["AuditRow"][];
            nextCursor: null | string;
        };
        AuditRow: {
            id: string;
            /** Format: date-time */
            at: string;
            actor: string;
            actorKind: string;
            action: string;
            targetKind: string;
            targetId: string;
            teamId: null | string;
            summary: components["schemas"]["JsonElement"];
        };
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
        JsonElement: unknown;
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
        LiveReconnect: {
            reason: string;
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
        MachineListResponse: {
            items: components["schemas"]["MachineResponse"][];
        };
        MachinePatchRequest: {
            labels?: null | {
                [key: string]: string;
            };
            pool?: null | string;
        };
        MachineReservationResponse: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            runId: string;
            nodeKey: string;
            /** Format: int64 */
            epoch: number | string;
            /** Format: date-time */
            expiresAt: string;
            releasePending: boolean;
        };
        MachineResponse: {
            /** Format: uuid */
            id: string;
            name: string;
            os: string;
            arch: string;
            /** Format: int32 */
            cores: number | string;
            /** Format: int32 */
            ramMb: number | string;
            /** Format: int32 */
            maxTasks: number | string;
            /** Format: int32 */
            slotsUsed: number | string;
            pool: string;
            labels: {
                [key: string]: string;
            };
            status: string;
            /** Format: date-time */
            lastHeartbeatAt: string;
            version: string;
            /** Format: double */
            cpuPercent: number | string;
            /** Format: int64 */
            ramUsedMb: number | string;
            /** Format: int64 */
            diskFreeMb: number | string;
            currentReservation: null | components["schemas"]["MachineReservationResponse"];
        };
        ManifestChange: {
            key: string;
            kind: components["schemas"]["ManifestChangeKind"];
            before: null | components["schemas"]["JsonElement"];
            after: null | components["schemas"]["JsonElement"];
        };
        /** @enum {unknown} */
        ManifestChangeKind: "Added" | "Removed" | "Changed";
        ManifestChanges: {
            nodes: components["schemas"]["ManifestChange"][];
            variables: components["schemas"]["ManifestChange"][];
            policies: components["schemas"]["ManifestChange"][];
            settings: components["schemas"]["ManifestChange"][];
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
        ReportAdded: {
            runId: string;
            reportId: string;
        };
        ReportCompareResponse: {
            /** Format: uuid */
            seriesId: string;
            left: components["schemas"]["ReportIndexItem"];
            right: components["schemas"]["ReportIndexItem"];
        };
        ReportIndexItem: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            runId: string;
            sourceKey: string;
            /** Format: int32 */
            attempt: number | string;
            kind: string;
            title: string;
            tags: string[];
            /** Format: int64 */
            sizeBytes: number | string;
            /** Format: date-time */
            createdAt: string;
        };
        ReportIndexResponse: {
            /** Format: uuid */
            runId: string;
            items: components["schemas"]["ReportIndexItem"][];
        };
        ReportReceipt: {
            /** Format: uuid */
            runId: string;
            /** Format: uuid */
            reportId: string;
        };
        RerunRunRequest: {
            fromNodes?: null | string[];
        };
        ReservationQueueItem: {
            nodeKey: string;
            /** Format: int32 */
            position: number | string;
            /** Format: int32 */
            count: number | string;
            /** Format: date-time */
            queuedAt: string;
            /** Format: uuid */
            blockingReservationId: null | string;
            /** Format: uuid */
            blockingRunId: null | string;
        };
        ReservationQueueResponse: {
            /** Format: uuid */
            runId: string;
            items: components["schemas"]["ReservationQueueItem"][];
        };
        ResourceAttemptSummary: {
            taskKey: string;
            /** Format: int32 */
            attempt: number | string;
            /** Format: uuid */
            agentId: null | string;
            /** Format: int64 */
            sampleCount: number | string;
            /** Format: double */
            averageCpuPercent: null | number | string;
            /** Format: double */
            maxCpuPercent: null | number | string;
            /** Format: double */
            peakRamBytes: null | number | string;
            /** Format: double */
            networkSentBytes: null | number | string;
            /** Format: double */
            networkReceivedBytes: null | number | string;
            /** Format: double */
            cpuObservedSeconds: number | string;
            /** Format: double */
            networkObservedSeconds: number | string;
            cpuUnavailableReason: null | string;
            memoryUnavailableReason: null | string;
            networkUnavailableReason: null | string;
        };
        ResourceObservation: {
            task: components["schemas"]["ResourcePoint"];
            machine: null | components["schemas"]["ResourcePoint"];
            machineUnavailableReason?: null | string;
        };
        ResourcePoint: {
            /** Format: date-time */
            atUtc: string;
            /** Format: double */
            intervalSeconds: null | number | string;
            /** Format: double */
            cpuPercent: null | number | string;
            /** Format: uint64 */
            ramBytes: null | number | string;
            /** Format: uint64 */
            networkSentBytes: null | number | string;
            /** Format: uint64 */
            networkReceivedBytes: null | number | string;
            /** Format: uint64 */
            networkSentDeltaBytes: null | number | string;
            /** Format: uint64 */
            networkReceivedDeltaBytes: null | number | string;
            /** Format: double */
            networkSentBytesPerSecond: null | number | string;
            /** Format: double */
            networkReceivedBytesPerSecond: null | number | string;
            cpuScope: string;
            memoryScope: string;
            networkScope: string;
            cpuUnavailableReason: null | string;
            memoryUnavailableReason: null | string;
            networkUnavailableReason: null | string;
            networkRateUnavailableReason: null | string;
        };
        ResourceSeriesPage: {
            samples: components["schemas"]["ResourceSeriesSample"][];
            nextCursor: null | string;
        };
        ResourceSeriesSample: {
            /** Format: uint64 */
            sequence: number | string;
            observation: components["schemas"]["ResourceObservation"];
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
            /** Format: uuid */
            parentRunId: null | string;
            reusedNodes: {
                [key: string]: string;
            };
        };
        RunTimelineAttempt: {
            nodeKey: string;
            taskKey: string;
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
            agentId: null | string;
        };
        RunTimelineResponse: {
            /** Format: uuid */
            runId: string;
            /** Format: int64 */
            lastSeq: number | string;
            status: string;
            attempts: components["schemas"]["RunTimelineAttempt"][];
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
        TaskOutputsResponse: {
            /** Format: uuid */
            runId: string;
            taskKey: string;
            /** Format: int32 */
            attempt: number | string;
            outputs: null | components["schemas"]["JsonElement"];
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
            machineIds: string[];
            /** Format: int32 */
            count: null | number | string;
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
        TriggerRunRequest: {
            workflowId: string;
            version?: null | string;
            variables?: Record<string, unknown>;
        };
        TriggerRunResponse: {
            /**
             * Format: uuid
             * @default 00000000-0000-0000-0000-000000000000
             */
            runId: string;
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
        /** @enum {string} */
        VariableType: "String" | "Int" | "Long" | "Double" | "Bool" | "Enum" | "StringList" | "Duration" | "Secret";
        WorkflowMetadata: {
            type: string;
            values: {
                [key: string]: string;
            };
        };
        WorkflowSourceDiff: {
            path: string;
            before: string;
            after: string;
            changed: boolean;
        };
        WorkflowVariable: {
            key: string;
            type: components["schemas"]["VariableType"];
            required: boolean;
            default: null | components["schemas"]["JsonElement"];
            allowedValues: string[];
        };
        WorkflowVariablesResponse: {
            workflowId: string;
            /** Format: uuid */
            versionId: string;
            version: string;
            variables: components["schemas"]["WorkflowVariable"][];
        };
        WorkflowVersionActivation: {
            workflowId: string;
            /** Format: uuid */
            activeVersionId: string;
            /** Format: uuid */
            latestVersionId: null | string;
        };
        WorkflowVersionDiff: {
            workflowId: string;
            /** Format: uuid */
            fromVersionId: string;
            /** Format: uuid */
            toVersionId: string;
            fromVersion: string;
            toVersion: string;
            manifest: components["schemas"]["ManifestChanges"];
            source: components["schemas"]["WorkflowSourceDiff"];
        };
        WorkflowVersionPage: {
            items: components["schemas"]["WorkflowVersionSummary"][];
            nextCursor: null | string;
            /** Format: uuid */
            activeVersionId: null | string;
            /** Format: uuid */
            latestVersionId: null | string;
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
            registration?: null | components["schemas"]["WorkflowVersionRegistration"];
        };
        WorkflowVersionRegistration: {
            gitCommit: string;
            gitAuthor: string;
            registeredBy: string;
            changeNote: null | string;
            sdkVersion: string;
            /** Format: date-time */
            registeredAt: string;
        };
        WorkflowVersionSummary: {
            /** Format: uuid */
            versionId: string;
            workflowId: string;
            version: string;
            registration: components["schemas"]["WorkflowVersionRegistration"];
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
