import { useEffect, useState } from "react";
import styles from "./live-usenet-connections.module.css";
import { receiveMessage } from "~/utils/websocket-util";
import { useNavigate } from "react-router";

const usenetConnectionsTopic = {'cxs': 'state'};

export function LiveUsenetConnections() {
    const navigate = useNavigate();
    const [connections, setConnections] = useState<string | null>(null);
    const parts = (connections || "0|1|0").split("|");
    const [live, max, idle] = parts.map(x => Number(x));
    const active = live - idle;
    const activePercent = 100 * (active / max);
    const livePercent = 100 * (live / max);

    // PERF FIX NEW-010: Add exponential backoff for WebSocket reconnection to reduce churn during network issues
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let attemptNumber = 0;

        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => setConnections(message));
            ws.onopen = () => {
                attemptNumber = 0;  // Reset on successful connection
                ws.send(JSON.stringify(usenetConnectionsTopic));
            };
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) navigate('/login');
            if (!disposed) {
                // Exponential backoff: 1s, 2s, 4s, 8s, 16s, max 30s
                const delay = Math.min(1000 * Math.pow(2, attemptNumber), 30000);
                setTimeout(() => connect(), delay);
                attemptNumber++;
            }
            setConnections(null);
        }
        return connect();
    }, [setConnections, navigate]);

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Usenet Connections
            </div>
            <div className={styles.bar}>
                <div className={styles.max} />
                <div className={styles.live} style={{ width: `${livePercent}%` }} />
                <div className={styles.active} style={{ width: `${activePercent}%` }} />
            </div>
            <div className={styles.caption}>
                {connections && `${live} connected / ${max} max`}
                {!connections && `Loading...`}
            </div>
            {connections &&
                <div className={styles.caption}>
                    ( {active} active )
                </div>
            }
        </div>
    );
}