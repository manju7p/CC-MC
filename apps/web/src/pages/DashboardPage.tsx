import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import type { ChillingCentreDto, DashboardSummaryDto } from "@cc-mc/shared-types";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

export function DashboardPage() {
  const { hasPermission } = useAuth();
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [centreId, setCentreId] = useState("");
  const [summary, setSummary] = useState<DashboardSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiFetch<ChillingCentreDto[]>("/centres")
      .then(setCentres)
      .catch(() => setCentres([]));
  }, []);

  useEffect(() => {
    setSummary(null);
    setError(null);
    const qs = centreId ? `?centreId=${centreId}` : "";
    apiFetch<DashboardSummaryDto>(`/dashboard/summary${qs}`)
      .then(setSummary)
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load dashboard"));
  }, [centreId]);

  if (error) return <p className="error">{error}</p>;
  if (!summary) return <p>Loading...</p>;

  const centreLabel =
    summary.centreIds.length === 0
      ? "no centre assigned"
      : centres.length > 0
        ? summary.centreIds
            .map((id) => centres.find((c) => c.id === id)?.name ?? `Centre ${id}`)
            .join(", ")
        : summary.centreIds.join(", ");

  return (
    <div>
      <h1>Today&apos;s Milk Reception</h1>
      <div className="dashboard-toolbar">
        <p className="hint">
          {summary.date} - {centreLabel}
        </p>
        {centres.length > 1 && (
          <label className="dashboard-centre-filter">
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

      {summary.totalTransactions === 0 ? (
        <div className="empty-state">
          <p>No reception transactions recorded yet today.</p>
          {hasPermission(PERMISSIONS.RECEPTION_CREATE) && (
            <p>
              <Link to="/reception">Record a new milk reception →</Link>
            </p>
          )}
        </div>
      ) : (
        <div className="kpi-row">
          <div className="kpi-tile">
            <span className="kpi-value">{summary.totalTransactions}</span>
            <span className="kpi-label">Transactions</span>
          </div>
          <div className="kpi-tile kpi-accepted">
            <span className="kpi-value">{summary.accepted}</span>
            <span className="kpi-label">Accepted</span>
          </div>
          <div className="kpi-tile kpi-rejected">
            <span className="kpi-value">{summary.rejected}</span>
            <span className="kpi-label">Rejected</span>
          </div>
          <div className="kpi-tile kpi-hold">
            <span className="kpi-value">{summary.hold}</span>
            <span className="kpi-label">On Hold</span>
          </div>
        </div>
      )}

      {summary.hold > 0 && hasPermission(PERMISSIONS.RECEPTION_OVERRIDE) && (
        <div className="callout callout-hold">
          <strong>{summary.hold}</strong> transaction{summary.hold === 1 ? "" : "s"} on HOLD need a manager decision.{" "}
          <Link to="/reception">Review in Reception →</Link>
        </div>
      )}
    </div>
  );
}
