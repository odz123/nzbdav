import { Modal, Button, Form, Alert } from "react-bootstrap";
import { useState, useEffect, useCallback } from "react";
import styles from "./server-modal.module.css";
import type { UsenetServerConfig } from "~/clients/backend-client.server";

type ServerModalProps = {
    show: boolean;
    server: UsenetServerConfig | null;
    existingServers: UsenetServerConfig[];
    onSave: (server: UsenetServerConfig) => void;
    onClose: () => void;
};

function generateId(): string {
    return `${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

function isPositiveInteger(value: string | number): boolean {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.toString().trim() === num.toString();
}

export function ServerModal({ show, server, existingServers, onSave, onClose }: ServerModalProps) {
    const isEditMode = server !== null;

    const [formData, setFormData] = useState<UsenetServerConfig>({
        id: generateId(),
        name: "",
        host: "",
        port: 563,
        useSsl: true,
        username: "",
        password: "",
        maxConnections: 50,
        priority: 0,
        enabled: true,
        retentionDays: 0,
        groups: ""
    });

    const [isTesting, setIsTesting] = useState(false);
    const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null);

    useEffect(() => {
        if (server) {
            setFormData(server);
        } else {
            // Reset to defaults for new server
            const maxPriority = existingServers.length > 0
                ? Math.max(...existingServers.map(s => s.priority))
                : -1;
            setFormData({
                id: generateId(),
                name: "",
                host: "",
                port: 563,
                useSsl: true,
                username: "",
                password: "",
                maxConnections: 50,
                priority: maxPriority + 1,
                enabled: true,
                retentionDays: 0,
                groups: ""
            });
        }
        setTestResult(null);
    }, [server, show, existingServers]);

    const validateForm = useCallback(() => {
        if (!formData.name.trim()) return "Name is required";
        if (!formData.host.trim()) return "Host is required";
        if (!isPositiveInteger(formData.port)) return "Port must be a positive integer";
        if (formData.port < 1 || formData.port > 65535) return "Port must be between 1 and 65535";
        if (!formData.username.trim()) return "Username is required";
        if (!formData.password.trim()) return "Password is required";
        if (!isPositiveInteger(formData.maxConnections)) return "Max Connections must be a positive integer";
        if (formData.maxConnections > 500) return "Max Connections should not exceed 500";
        if (!Number.isInteger(Number(formData.priority)) || Number(formData.priority) < 0) {
            return "Priority must be a non-negative integer";
        }
        if (formData.retentionDays < 0) return "Retention Days cannot be negative";
        return null;
    }, [formData]);

    const handleTest = useCallback(async () => {
        const error = validateForm();
        if (error) {
            setTestResult({ success: false, message: error });
            return;
        }

        setIsTesting(true);
        setTestResult(null);

        try {
            const response = await fetch("/api/test-usenet-connection", {
                method: "POST",
                body: (() => {
                    const form = new FormData();
                    form.append("host", formData.host);
                    form.append("port", formData.port.toString());
                    form.append("use-ssl", formData.useSsl.toString());
                    form.append("user", formData.username);
                    form.append("pass", formData.password);
                    return form;
                })()
            });

            const data = await response.json();
            const success = response.ok && data?.connected === true;

            setTestResult({
                success,
                message: success ? "Connection successful!" : "Connection failed. Please check your credentials."
            });
        } catch (error) {
            setTestResult({
                success: false,
                message: "Failed to test connection. Please try again."
            });
        } finally {
            setIsTesting(false);
        }
    }, [formData, validateForm]);

    const handleSave = useCallback(() => {
        const error = validateForm();
        if (error) {
            setTestResult({ success: false, message: error });
            return;
        }

        onSave(formData);
        onClose();
    }, [formData, validateForm, onSave, onClose]);

    const handleClose = useCallback(() => {
        setTestResult(null);
        onClose();
    }, [onClose]);

    return (
        <Modal show={show} onHide={handleClose} size="lg">
            <Modal.Header closeButton>
                <Modal.Title>{isEditMode ? "Edit Server" : "Add Server"}</Modal.Title>
            </Modal.Header>
            <Modal.Body>
                <Form>
                    <Form.Group className="mb-3">
                        <Form.Label>Name *</Form.Label>
                        <Form.Control
                            type="text"
                            placeholder="e.g., Primary Provider"
                            value={formData.name}
                            onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                        />
                        <Form.Text className="text-muted">
                            A friendly name to identify this server
                        </Form.Text>
                    </Form.Group>

                    <div className="row">
                        <div className="col-md-8">
                            <Form.Group className="mb-3">
                                <Form.Label>Host *</Form.Label>
                                <Form.Control
                                    type="text"
                                    placeholder="e.g., news.provider.com"
                                    value={formData.host}
                                    onChange={(e) => setFormData({ ...formData, host: e.target.value })}
                                />
                            </Form.Group>
                        </div>
                        <div className="col-md-4">
                            <Form.Group className="mb-3">
                                <Form.Label>Port *</Form.Label>
                                <Form.Control
                                    type="number"
                                    value={formData.port}
                                    onChange={(e) => setFormData({ ...formData, port: parseInt(e.target.value) || 0 })}
                                />
                            </Form.Group>
                        </div>
                    </div>

                    <Form.Group className="mb-3">
                        <Form.Check
                            type="checkbox"
                            label="Use SSL/TLS (recommended)"
                            checked={formData.useSsl}
                            onChange={(e) => setFormData({ ...formData, useSsl: e.target.checked })}
                        />
                        <Form.Text className="text-muted">
                            Standard ports: 119 (no SSL), 563 (SSL)
                        </Form.Text>
                    </Form.Group>

                    <Form.Group className="mb-3">
                        <Form.Label>Username *</Form.Label>
                        <Form.Control
                            type="text"
                            value={formData.username}
                            onChange={(e) => setFormData({ ...formData, username: e.target.value })}
                        />
                    </Form.Group>

                    <Form.Group className="mb-3">
                        <Form.Label>Password *</Form.Label>
                        <Form.Control
                            type="password"
                            value={formData.password}
                            onChange={(e) => setFormData({ ...formData, password: e.target.value })}
                        />
                    </Form.Group>

                    <div className="row">
                        <div className="col-md-6">
                            <Form.Group className="mb-3">
                                <Form.Label>Max Connections *</Form.Label>
                                <Form.Control
                                    type="number"
                                    value={formData.maxConnections}
                                    onChange={(e) => setFormData({ ...formData, maxConnections: parseInt(e.target.value) || 0 })}
                                />
                                <Form.Text className="text-muted">
                                    Check your provider's limit (typically 20-50)
                                </Form.Text>
                            </Form.Group>
                        </div>
                        <div className="col-md-6">
                            <Form.Group className="mb-3">
                                <Form.Label>Priority *</Form.Label>
                                <Form.Control
                                    type="number"
                                    value={formData.priority}
                                    onChange={(e) => setFormData({ ...formData, priority: parseInt(e.target.value) || 0 })}
                                />
                                <Form.Text className="text-muted">
                                    0 = highest priority (used first)
                                </Form.Text>
                            </Form.Group>
                        </div>
                    </div>

                    <div className="row">
                        <div className="col-md-6">
                            <Form.Group className="mb-3">
                                <Form.Label>Retention Days (optional)</Form.Label>
                                <Form.Control
                                    type="number"
                                    placeholder="0"
                                    value={formData.retentionDays || ""}
                                    onChange={(e) => setFormData({ ...formData, retentionDays: parseInt(e.target.value) || 0 })}
                                />
                                <Form.Text className="text-muted">
                                    How many days of posts this server retains (0 = unknown)
                                </Form.Text>
                            </Form.Group>
                        </div>
                        <div className="col-md-6">
                            <Form.Group className="mb-3">
                                <Form.Label>Groups/Tags (optional)</Form.Label>
                                <Form.Control
                                    type="text"
                                    placeholder="e.g., primary,backup"
                                    value={formData.groups}
                                    onChange={(e) => setFormData({ ...formData, groups: e.target.value })}
                                />
                                <Form.Text className="text-muted">
                                    Comma-separated categories for organization
                                </Form.Text>
                            </Form.Group>
                        </div>
                    </div>

                    <Form.Group className="mb-3">
                        <Form.Check
                            type="checkbox"
                            label="Enable this server"
                            checked={formData.enabled}
                            onChange={(e) => setFormData({ ...formData, enabled: e.target.checked })}
                        />
                    </Form.Group>

                    {testResult && (
                        <Alert variant={testResult.success ? "success" : "danger"}>
                            {testResult.message}
                        </Alert>
                    )}
                </Form>
            </Modal.Body>
            <Modal.Footer>
                <Button
                    variant="outline-primary"
                    onClick={handleTest}
                    disabled={isTesting}
                >
                    {isTesting ? "Testing..." : "Test Connection"}
                </Button>
                <Button variant="secondary" onClick={handleClose}>
                    Cancel
                </Button>
                <Button variant="primary" onClick={handleSave}>
                    {isEditMode ? "Save Changes" : "Add Server"}
                </Button>
            </Modal.Footer>
        </Modal>
    );
}
