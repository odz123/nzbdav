import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { ServerHealth } from "./components/server-health/server-health";
import { useCallback, useEffect, useState, useMemo } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Alert } from "react-bootstrap";
import type { ServerHealthInfo } from "~/clients/backend-client.server";

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
}

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config, serverHealthData] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey]),
        backendClient.getServerHealth().catch(() => ({ status: false, servers: [] }))
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        queueItems: queueData.items,
        historyStats: historyData.stats,
        historyItems: historyData.items,
        serverHealth: serverHealthData.servers,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueItems, setQueueItems] = useState(loaderData.queueItems);
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [serverHealth, setServerHealth] = useState<ServerHealthInfo[]>(loaderData.serverHealth);

    // effects
    useEffect(() => {
        if (queueItems.length >= 15) return;
        const refetchData = async () => {
            var response = await fetch('/api/get-health-check-queue?pageSize=30');
            if (response.ok) {
                const healthCheckQueue = await response.json();
                setQueueItems(healthCheckQueue.items);
                setUncheckedCount(healthCheckQueue.uncheckedCount);
            }
        };
        refetchData();
    }, [queueItems, setQueueItems]);

    // PERF FIX NEW-006: Add data comparison before setState to prevent unnecessary re-renders
    // Poll server health every 10 seconds
    useEffect(() => {
        const refetchServerHealth = async () => {
            try {
                const response = await fetch('/api/get-server-health');
                if (response.ok) {
                    const data = await response.json();
                    setServerHealth(prev => {
                        // Only update if data actually changed
                        if (JSON.stringify(prev) === JSON.stringify(data.servers)) {
                            return prev;
                        }
                        return data.servers;
                    });
                }
            } catch (error) {
                // Failed to fetch health data, keep previous state
            }
        };

        const interval = setInterval(refetchServerHealth, 10000);
        return () => clearInterval(interval);
    }, []);

    // events
    const onHealthItemStatus = useCallback(async (message: string) => {
        const [davItemId, healthResult, repairAction] = message.split('|');
        setQueueItems(x => x.filter(item => item.id !== davItemId));
        setUncheckedCount(x => x - 1);
        setHistoryStats(x => {
            const healthResultNum = Number(healthResult);
            const repairActionNum = Number(repairAction);

            // attempt to find and update a matching statistic
            let updated = false;
            const newStats = x.map(stat => {
                if (stat.result === healthResultNum && stat.repairStatus === repairActionNum) {
                    updated = true;
                    return { ...stat, count: stat.count + 1 };
                }
                return stat;
            });

            // if no statistic was updated, add a new one
            if (!updated) {
                return [
                    ...x,
                    {
                        result: healthResultNum,
                        repairStatus: repairActionNum,
                        count: 1
                    }
                ];
            }

            // if an update occurred, return the modified array
            return newStats;
        });
    }, [setQueueItems, setHistoryStats]);

    // PERF FIX NEW-002: Fix O(n²) state update - replace findIndex + filter + map with simple O(n) map
    const onHealthItemProgress = useCallback((message: string) => {
        const [davItemId, progress] = message.split('|');
        if (progress === "done") return;
        setQueueItems(queueItems => {
            return queueItems.map(item =>
                item.id === davItemId
                    ? { ...item, progress: Number(progress) }
                    : item
            );
        });
    }, []);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress
    ]);

    // PERF FIX NEW-010: Add exponential backoff for WebSocket reconnection to reduce churn during network issues
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let attemptNumber = 0;

        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => {
                attemptNumber = 0;  // Reset on successful connection
                ws.send(JSON.stringify(topicSubscriptions));
            };
            ws.onclose = () => {
                if (!disposed) {
                    // Exponential backoff: 1s, 2s, 4s, 8s, 16s, max 30s
                    const delay = Math.min(1000 * Math.pow(2, attemptNumber), 30000);
                    setTimeout(() => connect(), delay);
                    attemptNumber++;
                }
            };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }

        return connect();
    }, [onWebsocketMessage]);

    // PERF FIX NEW-005: Add useMemo for filtered list to prevent re-creating array on every render
    const topTenItems = useMemo(() => queueItems.slice(0, 10), [queueItems]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <ServerHealth servers={serverHealth} />
            </div>
            <div className={styles.section}>
                <HealthStats stats={historyStats} />
            </div>
            {isEnabled && uncheckedCount > 20 &&
                <Alert className={styles.alert} variant={'warning'}>
                    <b>Attention</b>
                    <ul className={styles.list}>
                        <li className={styles.listItem}>
                            You have ~{uncheckedCount} files whose health has never been determined.
                        </li>
                        <li className={styles.listItem}>
                            The queue will run an initial health check of these files.
                        </li>
                        <li className={styles.listItem}>
                            Under normal operation, health checks will occur much less frequently.
                        </li>
                    </ul>
                </Alert>
            }
            <div className={styles.section}>
                <HealthTable isEnabled={isEnabled} healthCheckItems={topTenItems} />
            </div>
        </div>
    );
}