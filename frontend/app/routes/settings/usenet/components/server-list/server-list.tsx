import { Button, Table, Badge, Form } from "react-bootstrap";
import styles from "./server-list.module.css";
import type { UsenetServerConfig } from "~/clients/backend-client.server";

type ServerListProps = {
    servers: UsenetServerConfig[];
    onEdit: (server: UsenetServerConfig) => void;
    onDelete: (serverId: string) => void;
    onToggleEnabled: (serverId: string, enabled: boolean) => void;
    onTest: (server: UsenetServerConfig) => void;
    testingServerId?: string | null;
};

export function ServerList({ servers, onEdit, onDelete, onToggleEnabled, onTest, testingServerId }: ServerListProps) {
    const sortedServers = [...servers].sort((a, b) => a.priority - b.priority);

    if (servers.length === 0) {
        return (
            <div className={styles.emptyState}>
                <p>No Usenet servers configured.</p>
                <p className="text-muted">Click "Add Server" to configure your first server.</p>
            </div>
        );
    }

    return (
        <Table striped bordered hover className={styles.table}>
            <thead>
                <tr>
                    <th style={{ width: '40px' }}>Enabled</th>
                    <th style={{ width: '60px' }}>Priority</th>
                    <th>Name</th>
                    <th>Host</th>
                    <th>Port</th>
                    <th style={{ width: '50px' }}>SSL</th>
                    <th>Username</th>
                    <th style={{ width: '120px' }}>Max Conn.</th>
                    <th style={{ width: '180px' }}>Actions</th>
                </tr>
            </thead>
            <tbody>
                {sortedServers.map((server) => (
                    <tr key={server.id} className={!server.enabled ? styles.disabledRow : ''}>
                        <td className="text-center">
                            <Form.Check
                                type="switch"
                                id={`enabled-${server.id}`}
                                checked={server.enabled}
                                onChange={(e) => onToggleEnabled(server.id, e.target.checked)}
                            />
                        </td>
                        <td className="text-center">
                            <Badge bg="secondary">{server.priority}</Badge>
                        </td>
                        <td>
                            <strong>{server.name}</strong>
                            {server.retentionDays > 0 && (
                                <div className="text-muted small">
                                    Retention: {server.retentionDays} days
                                </div>
                            )}
                        </td>
                        <td className="font-monospace">{server.host}</td>
                        <td className="text-center">{server.port}</td>
                        <td className="text-center">
                            {server.useSsl ? (
                                <Badge bg="success">Yes</Badge>
                            ) : (
                                <Badge bg="secondary">No</Badge>
                            )}
                        </td>
                        <td className="font-monospace">{server.username}</td>
                        <td className="text-center">{server.maxConnections}</td>
                        <td>
                            <div className={styles.actions}>
                                <Button
                                    size="sm"
                                    variant="outline-primary"
                                    onClick={() => onTest(server)}
                                    disabled={testingServerId === server.id}
                                >
                                    {testingServerId === server.id ? "Testing..." : "Test"}
                                </Button>
                                <Button
                                    size="sm"
                                    variant="outline-secondary"
                                    onClick={() => onEdit(server)}
                                >
                                    Edit
                                </Button>
                                <Button
                                    size="sm"
                                    variant="outline-danger"
                                    onClick={() => onDelete(server.id)}
                                >
                                    Delete
                                </Button>
                            </div>
                        </td>
                    </tr>
                ))}
            </tbody>
        </Table>
    );
}
