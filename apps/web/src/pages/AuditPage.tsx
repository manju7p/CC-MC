import { Fragment, useEffect, useState } from "react";
import type { AuditLogDto, ChillingCentreDto } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";

// The exact resourceType strings actually written by AuditService.record()
// call sites across the API (reception.service.ts, quality-rules.service.ts,
// sources.service.ts, vehicles.service.ts, auth.service.ts) - not invented,
// grepped from the current repository. Offered as a filter dropdown mapping
// directly to GET /audit-logs?resourceType=, which the backend already
// implements (audit.controller.ts) - no new query parameter introduced.
const RESOURCE_TYPES = ["MilkReceptionTransaction", "QualityRule", "Source", "Vehicle", "User"];

/**
 * Audit page (Checkpoint 6C). Rendered only when the route is reached -
 * gated from the nav by AUDIT_VIEW (see NavBar.tsx); the backend
 * independently enforces AUDIT_VIEW on every request regardless (Rule 2).
 */
export function AuditPage() {
  const [logs, setLogs] = useState<AuditLogDto[] | null>(null);
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [resourceType, setResourceType] = useState("");
  const [centreId, setCentreId] = useState("");
  const [expandedId, setExpandedId] = useState<number | null>(null);

  useEffect(() => {
    apiFetch<ChillingCentreDto[]>("/centres")
      .then(setCentres)
      .catch(() => setCentres([]));
  }, []);

  const load = () => {
    setLogs(null);
    setError(null);
    const params = new URLSearchParams();
    if (resourceType) params.set("resourceType", resourceType);
    if (centreId) params.set("centreId", centreId);
    const qs = params.toString();
    apiFetch<AuditLogDto[]>(`/audit-logs${qs ? `?${qs}` : ""}`)
      .then(setLogs)
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load audit logs"));
  };

  useEffect(load, [resourceType, centreId]);

  return (
    <div>
      <h1>Audit Log</h1>
      <p className="hint">Most recent 200 events visible to your account, newest first.</p>

      <div className="filter-row">
        <label>
          Resource type
          <select value={resourceType} onChange={(e) => setResourceType(e.target.value)}>
            <option value="">All</option>
            {RESOURCE_TYPES.map((rt) => (
              <option key={rt} value={rt}>
                {rt}
              </option>
            ))}
          </select>
        </label>
        {centres.length > 1 && (
          <label>
            Centre
            <select value={centreId} onChange={(e) => setCentreId(e.target.value)}>
              <option value="">All my centres</option>
              {centres.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>

      {error && <p className="error">{error}</p>}
      {!error && logs === null && <p>Loading...</p>}
      {!error && logs !== null && logs.length === 0 && <p className="hint">No audit events match this filter.</p>}

      {logs !== null && logs.length > 0 && (
        <table className="data-table">
          <thead>
            <tr>
              <th>When</th>
              <th>Action</th>
              <th>Resource</th>
              <th>User</th>
              <th>Centre</th>
              <th>Reason</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {logs.map((log) => (
              <Fragment key={log.id}>
                <tr>
                  <td>{new Date(log.createdAt).toLocaleString()}</td>
                  <td>{log.action}</td>
                  <td>
                    {log.resourceType} #{log.resourceId}
                  </td>
                  <td>{log.userId ?? "system"}</td>
                  <td>{log.centreId ?? "-"}</td>
                  <td>{log.reason ?? "-"}</td>
                  <td>
                    {(log.oldValue !== null || log.newValue !== null) && (
                      <button onClick={() => setExpandedId(expandedId === log.id ? null : log.id)}>
                        {expandedId === log.id ? "Hide" : "Details"}
                      </button>
                    )}
                  </td>
                </tr>
                {expandedId === log.id && (
                  <tr>
                    <td colSpan={7}>
                      <div className="audit-detail">
                        <div>
                          <strong>Before</strong>
                          <pre>{JSON.stringify(log.oldValue, null, 2)}</pre>
                        </div>
                        <div>
                          <strong>After</strong>
                          <pre>{JSON.stringify(log.newValue, null, 2)}</pre>
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
