import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import type { LocalGatewayStatusDto, LocalOutboxStatus, LocalTodaySummaryDto } from "@cc-mc/shared-types";
import {
  getLocalApiToken,
  getLocalApiUrl,
  LocalApiError,
  localApiFetch,
  setLocalApiToken,
  setLocalApiUrl,
} from "../api/localApiClient";

const POLL_INTERVAL_MS = 5000;

function outboxBadgeClass(status: LocalOutboxStatus): string {
  switch (status) {
    case "SYNCED":
      return "status-badge status-synced";
    case "FAILED":
      return "status-badge status-failed";
    case "PROCESSING":
      return "status-badge status-processing";
    case "PENDING":
    default:
      return "status-badge status-pending";
  }
}

/**
 * The operator's LOCAL dashboard - reads exclusively from the gateway's own
 * local (edge) HTTP API (apps/gateway/src/local-api/local-api-server.ts),
 * never from the cloud API. This is the page that must keep working when
 * the centre has no internet connectivity at all: everything shown here
 * comes from gateway.sqlite via the gateway process running on this same
 * machine/LAN, not from apps/api or PostgreSQL.
 *
 * Deliberately separate from DashboardPage.tsx (the existing CLOUD
 * dashboard, which reads GET /dashboard/summary from apps/api and shows
 * ACCEPTED/REJECTED/HOLD across all centres a user can see) - this page
 * shows ONE centre's local operational state as this one gateway sees it,
 * and honestly does NOT show ACCEPTED/HOLD/REJECTED, because the local
 * schema has no such field yet (see local-api-server.ts's toSummaryDtos()
 * doc comment). Showing both pages side by side, rather than merging them,
 * keeps that distinction visible instead of papering over it.
 */
export function LocalDashboardPage() {
  const [localApiUrlInput, setLocalApiUrlInput] = useState(getLocalApiUrl());
  const [localApiTokenInput, setLocalApiTokenInput] = useState(getLocalApiToken() ?? "");
  const [configured, setConfigured] = useState(Boolean(getLocalApiToken()));

  const [status, setStatus] = useState<LocalGatewayStatusDto | null>(null);
  const [today, setToday] = useState<LocalTodaySummaryDto | null>(null);
  const [connectionError, setConnectionError] = useState<string | null>(null);
  const [lastUpdatedAt, setLastUpdatedAt] = useState<Date | null>(null);

  const poll = useCallback(async () => {
    if (!getLocalApiToken()) return;
    try {
      const [statusDto, todayDto] = await Promise.all([
        localApiFetch<LocalGatewayStatusDto>("/local/status"),
        localApiFetch<LocalTodaySummaryDto>("/local/today"),
      ]);
      setStatus(statusDto);
      setToday(todayDto);
      setConnectionError(null);
      setLastUpdatedAt(new Date());
    } catch (err) {
      setConnectionError(err instanceof LocalApiError ? err.message : "Could not reach the local gateway.");
    }
  }, []);

  useEffect(() => {
    if (!configured) return;
    poll();
    const interval = setInterval(poll, POLL_INTERVAL_MS);
    return () => clearInterval(interval);
  }, [configured, poll]);

  const handleSaveSettings = (e: FormEvent) => {
    e.preventDefault();
    setLocalApiUrl(localApiUrlInput.trim() || getLocalApiUrl());
    setLocalApiToken(localApiTokenInput.trim() || null);
    setConfigured(Boolean(localApiTokenInput.trim()));
    setStatus(null);
    setToday(null);
    setConnectionError(null);
  };

  const isOffline = configured && connectionError !== null;

  return (
    <div>
      <h1>Local Dashboard</h1>
      <p className="hint">
        Reads directly from this centre&apos;s gateway (local SQLite), not the cloud. Keeps working with no internet
        connection.
      </p>

      <div className="inline-form">
        <form className="form local-settings-form" onSubmit={handleSaveSettings}>
          <label>
            Local gateway URL
            <input
              type="text"
              value={localApiUrlInput}
              onChange={(e) => setLocalApiUrlInput(e.target.value)}
              placeholder="http://localhost:4100"
            />
          </label>
          <label>
            Local gateway access token
            <input
              type="password"
              value={localApiTokenInput}
              onChange={(e) => setLocalApiTokenInput(e.target.value)}
              placeholder="Provided by whoever set up this gateway"
            />
          </label>
          <button type="submit">Save & connect</button>
        </form>
      </div>

      {!configured && (
        <div className="empty-state">
          <p>Enter this gateway&apos;s local API URL and access token above to see local operational data.</p>
        </div>
      )}

      {configured && (
        <>
          <div className={isOffline ? "connectivity-banner connectivity-offline" : "connectivity-banner connectivity-online"}>
            {isOffline ? (
              <>
                <strong>Offline / gateway unreachable.</strong> {connectionError} Local data will resume updating once
                the gateway is reachable again.
              </>
            ) : (
              <>
                <strong>Connected to local gateway.</strong>{" "}
                {status && (
                  <>
                    Gateway {status.gatewayId} (centre {status.centreId}), cloud sync:{" "}
                    <span className="hint">{status.cloudConnectivity.toLowerCase()}</span>.
                  </>
                )}
                {lastUpdatedAt && <span className="hint"> Updated {lastUpdatedAt.toLocaleTimeString()}.</span>}
              </>
            )}
          </div>

          {today && (
            <>
              <div className="dashboard-toolbar">
                <p className="hint">Today ({today.dateLabel}, IST)</p>
              </div>

              {today.totalTransactions === 0 ? (
                <div className="empty-state">
                  <p>No reception transactions recorded locally yet today.</p>
                </div>
              ) : (
                <div className="kpi-row">
                  <div className="kpi-tile">
                    <span className="kpi-value">{today.totalQuantityKg.toFixed(1)} kg</span>
                    <span className="kpi-label">Total collection</span>
                  </div>
                  <div className="kpi-tile">
                    <span className="kpi-value">{today.totalTransactions}</span>
                    <span className="kpi-label">Transactions today</span>
                  </div>
                  <div className="kpi-tile">
                    <span className="kpi-value">{status?.pendingSyncCount ?? "-"}</span>
                    <span className="kpi-label">Pending cloud sync</span>
                  </div>
                </div>
              )}

              {today.transactions.length > 0 && (
                <>
                  <h2>Recent transactions</h2>
                  <p className="hint">
                    Accepted/HOLD/Rejected status is decided by the cloud during sync and is not yet reported back to
                    this gateway - only local sync state is shown below.
                  </p>
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th>Captured at</th>
                        <th>Vehicle</th>
                        <th>Source</th>
                        <th>Qty (kg)</th>
                        <th>Fat</th>
                        <th>SNF</th>
                        <th>Temp</th>
                        <th>Sync status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {today.transactions.map((t) => (
                        <tr key={t.id}>
                          <td>{new Date(t.capturedAt).toLocaleTimeString()}</td>
                          <td>{t.vehicleId}</td>
                          <td>{t.sourceId}</td>
                          <td>{t.quantityKg}</td>
                          <td>{t.fat}</td>
                          <td>{t.snf}</td>
                          <td>{t.temperature}</td>
                          <td>
                            <span className={outboxBadgeClass(t.outboxStatus)}>{t.outboxStatus}</span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
}
