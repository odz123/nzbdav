import { Alert, Form } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"];
    const arrConfig = JSON.parse(config["arr.instances"]);
    const areArrInstancesConfigured =
        arrConfig.RadarrInstances.length > 0 ||
        arrConfig.SonarrInstances.length > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    var helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library-Directory and Radarr/Sonarr instances are configured.";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    label={`Enable Background Repairs`}
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })} />
                <Form.Text id="enable-repairs-help" muted>
                    {helpText}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="library-dir-input">Library Directory</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <Form.Text id="library-dir-help" muted>
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your NzbDAV container.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="repairs-connections-input">Max Connections for Health Checks</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidRepairsConnections(config["repair.connections"]) && styles.error])}
                    type="text"
                    id="repairs-connections-input"
                    aria-describedby="repairs-connections-help"
                    placeholder={"All"}
                    value={config["repair.connections"] || ""}
                    onChange={e => setNewConfig({ ...config, "repair.connections": e.target.value })} />
                <Form.Text id="repairs-connections-help" muted>
                    The background health-check job will not use any more than this number of connections. Will default to your overall Max Connections if left empty.
                </Form.Text>
            </Form.Group>

            <hr />
            <Alert variant="info" className="mb-3">
                <strong>Performance Optimizations</strong> - These settings control validation speed and accuracy
            </Alert>

            <Form.Group>
                <Form.Label htmlFor="sampling-rate-input">Sampling Rate (%)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidSamplingRate(config["repair.sampling-rate"]) && styles.error])}
                    type="text"
                    id="sampling-rate-input"
                    aria-describedby="sampling-rate-help"
                    placeholder={"15"}
                    value={config["repair.sampling-rate"] ? (parseFloat(config["repair.sampling-rate"]) * 100).toString() : ""}
                    onChange={e => {
                        const percent = parseFloat(e.target.value);
                        const decimal = !isNaN(percent) ? (percent / 100).toString() : e.target.value;
                        setNewConfig({ ...config, "repair.sampling-rate": decimal });
                    }} />
                <Form.Text id="sampling-rate-help" muted>
                    Percentage of segments to validate (5-100%). Lower = faster checks, higher = more accurate. Default: 15%
                </Form.Text>
            </Form.Group>

            <Form.Group>
                <Form.Label htmlFor="min-segments-input">Minimum Segments to Check</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMinSegments(config["repair.min-segments"]) && styles.error])}
                    type="text"
                    id="min-segments-input"
                    aria-describedby="min-segments-help"
                    placeholder={"10"}
                    value={config["repair.min-segments"] || ""}
                    onChange={e => setNewConfig({ ...config, "repair.min-segments": e.target.value })} />
                <Form.Text id="min-segments-help" muted>
                    Minimum segments to always check regardless of sampling rate. Ensures small files are validated thoroughly. Default: 10
                </Form.Text>
            </Form.Group>

            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="adaptive-sampling-checkbox"
                    aria-describedby="adaptive-sampling-help"
                    label={`Enable Adaptive Sampling`}
                    checked={config["repair.adaptive-sampling"] !== "false"}
                    onChange={e => setNewConfig({ ...config, "repair.adaptive-sampling": "" + e.target.checked })} />
                <Form.Text id="adaptive-sampling-help" muted>
                    Automatically adjust sampling rate based on file age. Newer files are checked more thoroughly. Default: Enabled
                </Form.Text>
            </Form.Group>

            <hr />
            <Alert variant="info" className="mb-3">
                <strong>Segment Caching</strong> - Cache validated segments to speed up re-checks
            </Alert>

            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="cache-enabled-checkbox"
                    aria-describedby="cache-enabled-help"
                    label={`Enable Healthy Segment Cache`}
                    checked={config["repair.cache-enabled"] !== "false"}
                    onChange={e => setNewConfig({ ...config, "repair.cache-enabled": "" + e.target.checked })} />
                <Form.Text id="cache-enabled-help" muted>
                    Cache successfully validated segments to skip re-checking them. Dramatically speeds up repeated health checks. Default: Enabled
                </Form.Text>
            </Form.Group>

            <Form.Group>
                <Form.Label htmlFor="cache-ttl-input">Cache Lifetime (hours)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidCacheTtl(config["repair.cache-ttl-hours"]) && styles.error])}
                    type="text"
                    id="cache-ttl-input"
                    aria-describedby="cache-ttl-help"
                    placeholder={"24"}
                    value={config["repair.cache-ttl-hours"] || ""}
                    onChange={e => setNewConfig({ ...config, "repair.cache-ttl-hours": e.target.value })} />
                <Form.Text id="cache-ttl-help" muted>
                    How long to cache validated segments (1-168 hours). Longer = more cache hits, shorter = fresher validation. Default: 24 hours
                </Form.Text>
            </Form.Group>

            <hr />
            <Alert variant="info" className="mb-3">
                <strong>Parallel Processing</strong> - Check multiple files simultaneously
            </Alert>

            <Form.Group>
                <Form.Label htmlFor="parallel-files-input">Parallel File Count</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidParallelFiles(config["repair.parallel-files"]) && styles.error])}
                    type="text"
                    id="parallel-files-input"
                    aria-describedby="parallel-files-help"
                    placeholder={"3"}
                    value={config["repair.parallel-files"] || ""}
                    onChange={e => setNewConfig({ ...config, "repair.parallel-files": e.target.value })} />
                <Form.Text id="parallel-files-help" muted>
                    Number of files to validate simultaneously (1-10). Higher = faster queue processing but more connection usage. Default: 3
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["repair.connections"] !== newConfig["repair.connections"]
        || config["media.library-dir"] !== newConfig["media.library-dir"]
        || config["repair.sampling-rate"] !== newConfig["repair.sampling-rate"]
        || config["repair.min-segments"] !== newConfig["repair.min-segments"]
        || config["repair.adaptive-sampling"] !== newConfig["repair.adaptive-sampling"]
        || config["repair.cache-enabled"] !== newConfig["repair.cache-enabled"]
        || config["repair.cache-ttl-hours"] !== newConfig["repair.cache-ttl-hours"]
        || config["repair.parallel-files"] !== newConfig["repair.parallel-files"];
}

export function isRepairsSettingsValid(newConfig: Record<string, string>) {
    return isValidRepairsConnections(newConfig["repair.connections"])
        && isValidSamplingRate(newConfig["repair.sampling-rate"])
        && isValidMinSegments(newConfig["repair.min-segments"])
        && isValidCacheTtl(newConfig["repair.cache-ttl-hours"])
        && isValidParallelFiles(newConfig["repair.parallel-files"]);
}

function isValidRepairsConnections(repairsConnections: string): boolean {
    return repairsConnections === "" || isNonNegativeInteger(repairsConnections);
}

function isValidSamplingRate(samplingRate: string): boolean {
    if (samplingRate === "") return true;
    const num = parseFloat(samplingRate);
    return !isNaN(num) && num >= 0.05 && num <= 1.0;
}

function isValidMinSegments(minSegments: string): boolean {
    if (minSegments === "") return true;
    const num = parseInt(minSegments);
    return Number.isInteger(num) && num >= 1 && num <= 100;
}

function isValidCacheTtl(cacheTtl: string): boolean {
    if (cacheTtl === "") return true;
    const num = parseInt(cacheTtl);
    return Number.isInteger(num) && num >= 1 && num <= 168;
}

function isValidParallelFiles(parallelFiles: string): boolean {
    if (parallelFiles === "") return true;
    const num = parseInt(parallelFiles);
    return Number.isInteger(num) && num >= 1 && num <= 10;
}

function isNonNegativeInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && value.trim() === num.toString();
}
