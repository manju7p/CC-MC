import { useEffect, useState } from "react";
import type { DashboardSummaryDto } from "@cc-mc/shared-types";
import { apiFetch } from "../api/client";

export function DashboardPage() {
  const [summary, setSummary] = useState<DashboardSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiFetch<DashboardSummaryDto>("/dashboard/summary")
      .then(setSummary)
      .catch((err) => setError(err.message));
  }, []);

  if (error) return <p className="error">{error}</p>;
  if (!summary) return <p>Loading...</p>;

  return (
    <div>
      <h1>Today&apos;s Milk Reception</h1>
      <p className="hint">{summary.date} - centre(s): {summary.centreIds.join(", ") || "none assigned"}</p>
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
    </div>
  );
}
