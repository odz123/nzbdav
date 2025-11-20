class BackendClient {
    public async isOnboarding(): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/is-onboarding";

        const response = await fetch(url, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            }
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to fetch onboarding status: ${data.error || response.statusText}`);
        }

        return data.isOnboarding;
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/create-account";

        const response = await fetch(url, {
            method: "POST",
            headers: {
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to create account: ${data.error || response.statusText}`);
        }

        return data.status;
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/authenticate";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to authenticate: ${data.error || response.statusText}`);
        }

        return data.authenticated;
    }

    public async getQueue(limit: number): Promise<QueueResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=queue&limit=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get queue: ${data.error || response.statusText}`);
        }

        return data.queue;
    }

    public async getHistory(limit: number): Promise<HistoryResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=history&pageSize=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get history: ${data.error || response.statusText}`);
        }

        return data.history;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        var config = await this.getConfig(["api.manual-category"]);
        var category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const url = process.env.BACKEND_URL + `/api?mode=addfile&cat=${category}&priority=0&pp=0`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("nzbFile", nzbFile, nzbFile.name);
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to add nzb file: ${data.error || response.statusText}`);
        }

        if (!data.nzo_ids || data.nzo_ids.length != 1) {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return data.nzo_ids[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const url = process.env.BACKEND_URL + "/api/list-webdav-directory";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("directory", directory);
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to list webdav directory: ${data.error || response.statusText}`);
        }

        return data.items;
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const url = process.env.BACKEND_URL + "/api/get-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const key of keys) {
                    form.append("config-keys", key);
                }
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get config items: ${data.error || response.statusText}`);
        }

        return data.configItems || [];
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/update-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const item of configItems) {
                    form.append(item.configName, item.configValue);
                }
                return form;
            })()
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to update config items: ${data.error || response.statusText}`);
        }

        return data.status;
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-queue";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get health check queue: ${data.error || response.statusText}`);
        }

        return data;
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-history";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get health check history: ${data.error || response.statusText}`);
        }

        return data;
    }

    public async getServerHealth(): Promise<ServerHealthResponse> {
        const url = process.env.BACKEND_URL + "/api/get-server-health";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        const data = await response.json();
        if (!response.ok) {
            throw new Error(`Failed to get server health: ${data.error || response.statusText}`);
        }

        return data;
    }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
    slots: QueueSlot[],
    noofslots: number,
}

export type QueueSlot = {
    nzo_id: string,
    priority: string,
    filename: string,
    cat: string,
    percentage: string,
    true_percentage: string,
    status: string,
    mb: string,
    mbleft: string,
}

export type HistoryResponse = {
    slots: HistorySlot[],
    noofslots: number,
}

export type HistorySlot = {
    nzo_id: string,
    nzb_name: string,
    name: string,
    category: string,
    status: string,
    bytes: number,
    storage: string,
    download_time: number,
    fail_message: string,
}

export type DirectoryItem = {
    name: string,
    isDirectory: boolean,
    size: number | null | undefined,
}

export type ConfigItem = {
    configName: string,
    configValue: string,
}

export type TestUsenetConnectionRequest = {
    host: string,
    port: string,
    useSsl: string,
    user: string,
    pass: string
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number,
    items: HealthCheckQueueItem[]
}

export type HealthCheckQueueItem = {
    id: string,
    name: string,
    path: string,
    releaseDate: string | null,
    lastHealthCheck: string | null,
    nextHealthCheck: string | null,
    progress: number,
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[],
    items: HealthCheckResult[]
}

export type HealthCheckStats = {
    result: HealthResult,
    repairStatus: RepairAction,
    count: number
}

export type HealthCheckResult = {
    id: string,
    createdAt: string,
    davItemId: string,
    path: string,
    result: HealthResult,
    repairStatus: RepairAction,
    message: string | null
}

export enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

export enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}

export type ServerHealthResponse = {
    status: boolean,
    servers: ServerHealthInfo[]
}

export type ServerHealthInfo = {
    id: string,
    name: string,
    host: string,
    port: number,
    priority: number,
    maxConnections: number,
    isAvailable: boolean,
    consecutiveFailures: number,
    totalSuccesses: number,
    totalFailures: number,
    lastSuccessTime: string | null,
    lastFailureTime: string | null,
    lastException: string | null
}

export type UsenetServerConfig = {
    id: string,
    name: string,
    host: string,
    port: number,
    useSsl: boolean,
    username: string,
    password: string,
    maxConnections: number,
    priority: number,
    enabled: boolean,
    retentionDays: number,
    groups: string
}
