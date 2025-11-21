import { Card, Badge, Table } from "react-bootstrap";
import styles from "./server-health.module.css";
import type { ServerHealthInfo } from "~/clients/backend-client.server";

export function ServerHealth({ servers }: { servers: ServerHealthInfo[] }) {
    if (!servers || servers.length === 0) {
        return null;
    }

    const formatTimestamp = (timestamp: string | null) => {
        if (!timestamp) return "Never";
        const date = new Date(timestamp);
        const now = new Date();
        const diff = now.getTime() - date.getTime();
        const minutes = Math.floor(diff / 60000);
        const hours = Math.floor(minutes / 60);
        const days = Math.floor(hours / 24);

        if (minutes < 1) return "Just now";
        if (minutes < 60) return `${minutes} minute${minutes !== 1 ? 's' : ''} ago`;
        if (hours < 24) return `${hours} hour${hours !== 1 ? 's' : ''} ago`;
        return `${days} day${days !== 1 ? 's' : ''} ago`;
    };

    const getCircuitBreakerStatus = (server: ServerHealthInfo) => {
        if (server.isAvailable && server.consecutiveFailures === 0) {
            return { text: "CLOSED (Normal)", variant: "success" };
        } else if (server.isAvailable && server.consecutiveFailures > 0) {
            return { text: "HALF-OPEN (Testing)", variant: "warning" };
        } else {
            return { text: "OPEN (Disabled)", variant: "danger" };
        }
    };

    const sortedServers = [...servers].sort((a, b) => a.priority - b.priority);

    return (
        <Card className={styles.card}>
            <Card.Header className={styles.header}>
                <h5 className="mb-0">Usenet Servers Status</h5>
            </Card.Header>
            <Card.Body className={styles.body}>
                {sortedServers.map((server, index) => {
                    const circuitBreaker = getCircuitBreakerStatus(server);
                    const successRate = server.totalSuccesses + server.totalFailures > 0
                        ? ((server.totalSuccesses / (server.totalSuccesses + server.totalFailures)) * 100).toFixed(1)
                        : "N/A";

                    return (
                        <div key={server.id} className={styles.serverSection}>
                            {index > 0 && <hr className={styles.divider} />}

                            <div className={styles.serverHeader}>
                                <div className={styles.serverTitle}>
                                    <h6 className={styles.serverName}>
                                        {server.name}
                                        <Badge bg={server.isAvailable ? "success" : "danger"} className="ms-2">
                                            {server.isAvailable ? "Available" : "Unavailable"}
                                        </Badge>
                                        <Badge bg="secondary" className="ms-1">
                                            Priority {server.priority}
                                        </Badge>
                                    </h6>
                                    <div className={styles.serverInfo}>
                                        {server.host}:{server.port}
                                    </div>
                                </div>
                            </div>

                            <Table size="sm" className={styles.statsTable}>
                                <tbody>
                                    <tr>
                                        <td className={styles.label}>Max Connections:</td>
                                        <td>{server.maxConnections}</td>
                                        <td className={styles.label}>Circuit Breaker:</td>
                                        <td>
                                            <Badge bg={circuitBreaker.variant as any}>
                                                {circuitBreaker.text}
                                            </Badge>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.label}>Successes:</td>
                                        <td className="text-success">
                                            {server.totalSuccesses.toLocaleString()}
                                        </td>
                                        <td className={styles.label}>Failures:</td>
                                        <td className="text-danger">
                                            {server.totalFailures.toLocaleString()}
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.label}>Success Rate:</td>
                                        <td>{successRate}%</td>
                                        <td className={styles.label}>Articles Not Found:</td>
                                        <td className={server.totalArticlesNotFound > 0 ? "text-warning" : ""}>
                                            {server.totalArticlesNotFound.toLocaleString()}
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.label}>Consecutive Failures:</td>
                                        <td colSpan={3} className={server.consecutiveFailures > 0 ? "text-danger" : ""}>
                                            {server.consecutiveFailures}
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.label}>Last Success:</td>
                                        <td>{formatTimestamp(server.lastSuccessTime)}</td>
                                        <td className={styles.label}>Last Failure:</td>
                                        <td>{formatTimestamp(server.lastFailureTime)}</td>
                                    </tr>
                                    {server.lastException && (
                                        <tr>
                                            <td className={styles.label}>Last Error:</td>
                                            <td colSpan={3} className="text-danger small">
                                                {server.lastException}
                                            </td>
                                        </tr>
                                    )}
                                </tbody>
                            </Table>
                        </div>
                    );
                })}
            </Card.Body>
        </Card>
    );
}
