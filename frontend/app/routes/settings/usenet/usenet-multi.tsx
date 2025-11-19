import { Button, Alert } from "react-bootstrap";
import styles from "./usenet.module.css";
import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { ServerList } from "./components/server-list/server-list";
import { ServerModal } from "./components/server-modal/server-modal";
import type { UsenetServerConfig } from "~/clients/backend-client.server";

type UsenetSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
    onReadyToSave: (isReadyToSave: boolean) => void;
};

function generateId(): string {
    return `${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

// Migrate legacy single-server config to multi-server format
function migrateLegacyConfig(config: Record<string, string>): UsenetServerConfig[] {
    // If we already have multi-server config, parse and return it
    if (config["usenet.servers"]) {
        try {
            const servers = JSON.parse(config["usenet.servers"]);
            if (Array.isArray(servers) && servers.length > 0) {
                return servers;
            }
        } catch (e) {
            console.error("Failed to parse usenet.servers:", e);
        }
    }

    // Check if we have legacy single-server config
    if (config["usenet.host"] && config["usenet.user"]) {
        return [{
            id: generateId(),
            name: "Primary Server",
            host: config["usenet.host"] || "",
            port: parseInt(config["usenet.port"]) || 563,
            useSsl: config["usenet.use-ssl"] === "true",
            username: config["usenet.user"] || "",
            password: config["usenet.pass"] || "",
            maxConnections: parseInt(config["usenet.connections"]) || 50,
            priority: 0,
            enabled: true,
            retentionDays: 0,
            groups: ""
        }];
    }

    // No config exists yet
    return [];
}

export function UsenetSettings({ config, setNewConfig, onReadyToSave }: UsenetSettingsProps) {
    const [servers, setServers] = useState<UsenetServerConfig[]>(() => migrateLegacyConfig(config));
    const [showModal, setShowModal] = useState(false);
    const [editingServer, setEditingServer] = useState<UsenetServerConfig | null>(null);
    const [hasChanges, setHasChanges] = useState(false);

    // Update parent config whenever servers change
    useEffect(() => {
        const serversJson = JSON.stringify(servers);
        const currentServersJson = config["usenet.servers"];

        if (serversJson !== currentServersJson) {
            setNewConfig({
                ...config,
                "usenet.servers": serversJson,
                // Also keep legacy fields for backward compatibility
                "usenet.connections-per-stream": config["usenet.connections-per-stream"] || "5"
            });
            setHasChanges(true);
        }
    }, [servers]);

    // Always ready to save (no connection test required for multi-server)
    useEffect(() => {
        onReadyToSave && onReadyToSave(true);
    }, [onReadyToSave]);

    const handleAddServer = useCallback(() => {
        setEditingServer(null);
        setShowModal(true);
    }, []);

    const handleEditServer = useCallback((server: UsenetServerConfig) => {
        setEditingServer(server);
        setShowModal(true);
    }, []);

    const handleDeleteServer = useCallback((serverId: string) => {
        if (confirm("Are you sure you want to delete this server?")) {
            setServers(prevServers => prevServers.filter(s => s.id !== serverId));
        }
    }, []);

    const handleToggleEnabled = useCallback((serverId: string, enabled: boolean) => {
        setServers(prevServers =>
            prevServers.map(s => s.id === serverId ? { ...s, enabled } : s)
        );
    }, []);

    const handleTestServer = useCallback(async (server: UsenetServerConfig) => {
        try {
            const response = await fetch("/api/test-usenet-connection", {
                method: "POST",
                body: (() => {
                    const form = new FormData();
                    form.append("host", server.host);
                    form.append("port", server.port.toString());
                    form.append("use-ssl", server.useSsl.toString());
                    form.append("user", server.username);
                    form.append("pass", server.password);
                    return form;
                })()
            });

            const data = await response.json();
            const success = response.ok && data?.connected === true;

            if (success) {
                alert(`Connection to ${server.name} successful!`);
            } else {
                alert(`Connection to ${server.name} failed. Please check your credentials.`);
            }
        } catch (error) {
            alert(`Failed to test connection to ${server.name}.`);
        }
    }, []);

    const handleSaveServer = useCallback((server: UsenetServerConfig) => {
        setServers(prevServers => {
            const existingIndex = prevServers.findIndex(s => s.id === server.id);
            if (existingIndex >= 0) {
                // Update existing server
                const newServers = [...prevServers];
                newServers[existingIndex] = server;
                return newServers;
            } else {
                // Add new server
                return [...prevServers, server];
            }
        });
    }, []);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingServer(null);
    }, []);

    return (
        <div className={styles.container}>
            {servers.length === 0 && (
                <Alert variant="info">
                    <Alert.Heading>Multi-Server Configuration</Alert.Heading>
                    <p>
                        Configure multiple Usenet servers with automatic failover. The system will try servers
                        in priority order (0 = highest) and automatically fail over to backup servers if articles
                        are not found or if a server is unavailable.
                    </p>
                    <p className="mb-0">
                        <strong>Get started by adding your first server below.</strong>
                    </p>
                </Alert>
            )}

            {servers.length > 0 && (
                <>
                    <Alert variant="secondary">
                        <strong>Multi-Server Failover:</strong> Servers are tried in priority order (0 = highest).
                        Lower priority servers are used as backups if articles are not found or if higher priority servers fail.
                        View server health and statistics on the <a href="/health">Health</a> page.
                    </Alert>

                    <ServerList
                        servers={servers}
                        onEdit={handleEditServer}
                        onDelete={handleDeleteServer}
                        onToggleEnabled={handleToggleEnabled}
                        onTest={handleTestServer}
                    />
                </>
            )}

            <div className={styles["justify-right"]} style={{ marginTop: '1rem' }}>
                <Button variant="primary" onClick={handleAddServer}>
                    Add Server
                </Button>
            </div>

            <ServerModal
                show={showModal}
                server={editingServer}
                existingServers={servers}
                onSave={handleSaveServer}
                onClose={handleCloseModal}
            />

            {servers.length > 0 && (
                <div style={{ marginTop: '1.5rem' }}>
                    <h6>Global Settings</h6>
                    <div className="mb-3">
                        <label className="form-label">Connections Per Stream</label>
                        <input
                            type="number"
                            className="form-control"
                            style={{ maxWidth: '200px' }}
                            placeholder="5"
                            value={config["usenet.connections-per-stream"] || "5"}
                            onChange={e => setNewConfig({ ...config, "usenet.connections-per-stream": e.target.value })}
                        />
                        <div className="form-text">
                            Number of connections to use per download stream (default: 5)
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.servers"] !== newConfig["usenet.servers"]
        || config["usenet.connections-per-stream"] !== newConfig["usenet.connections-per-stream"];
}
