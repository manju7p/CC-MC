import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import type { ChillingCentreDto } from "@cc-mc/shared-types";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { useAuth } from "../auth/AuthContext";
import { apiFetch } from "../api/client";

/**
 * Renders the centres this user actually has access to, per the real
 * backend model (RequestUser.centreAccess = { allCentres, centreIds } -
 * see rbac.types.ts). There is no "select one active centre" concept
 * anywhere in the API/session model, so this deliberately does not invent
 * one - it lists what GET /centres (already scoped server-side to this
 * user) returns, or "All centres" for an org-wide user.
 */
function CentreContext() {
  const { user } = useAuth();
  const [centres, setCentres] = useState<ChillingCentreDto[] | null>(null);

  useEffect(() => {
    if (!user) return;
    apiFetch<ChillingCentreDto[]>("/centres")
      .then(setCentres)
      .catch(() => setCentres([]));
  }, [user]);

  if (!user) return null;
  if (user.centreAccess.allCentres) {
    return <span className="centre-context">All centres</span>;
  }
  if (centres === null) {
    return <span className="centre-context hint">Loading centre(s)...</span>;
  }
  if (centres.length === 0) {
    return <span className="centre-context error">No centre assigned</span>;
  }
  return (
    <span className="centre-context" title={centres.map((c) => c.code).join(", ")}>
      {centres.map((c) => c.name).join(", ")}
    </span>
  );
}

export function NavBar() {
  const { user, hasPermission, logout } = useAuth();
  const navigate = useNavigate();

  if (!user) return null;

  const handleLogout = () => {
    logout();
    navigate("/login");
  };

  return (
    <nav className="navbar">
      <div className="navbar-links">
        {hasPermission(PERMISSIONS.DASHBOARD_VIEW) && <Link to="/">Dashboard</Link>}
        {hasPermission(PERMISSIONS.RECEPTION_VIEW) && <Link to="/local">Local Dashboard</Link>}
        {hasPermission(PERMISSIONS.RECEPTION_VIEW) && <Link to="/reception">Reception</Link>}
        {hasPermission(PERMISSIONS.SOURCE_VIEW) && <Link to="/sources">Sources</Link>}
        {hasPermission(PERMISSIONS.VEHICLE_VIEW) && <Link to="/vehicles">Vehicles</Link>}
        {hasPermission(PERMISSIONS.QUALITY_RULE_VIEW) && <Link to="/quality-rules">Quality Rules</Link>}
        {hasPermission(PERMISSIONS.AUDIT_VIEW) && <Link to="/audit">Audit</Link>}
      </div>
      <div className="navbar-user">
        <CentreContext />
        <span>
          {user.fullName} ({user.roles.join(", ")})
        </span>
        <button onClick={handleLogout}>Log out</button>
      </div>
    </nav>
  );
}
