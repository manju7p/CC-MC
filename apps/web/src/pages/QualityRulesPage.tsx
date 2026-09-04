import { useEffect, useState } from "react";
import { PERMISSIONS, type QualityRuleDto } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

/**
 * Quality Rules page (Checkpoint 6C). Uses only the existing
 * GET/PATCH /quality-rules endpoints (quality-rules.controller.ts) - there
 * is no create/delete route, so this page is list + edit only, matching
 * the actual API surface.
 *
 * QUALITY_RULE_CONFIGURE gates whether the Edit action even renders
 * (UX only - the backend independently re-checks on every PATCH, including
 * the global-rule-requires-allCentres rule in quality-rules.service.ts,
 * which this page cannot see in advance and does not try to replicate).
 */
export function QualityRulesPage() {
  const { hasPermission } = useAuth();
  const [rules, setRules] = useState<QualityRuleDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [draft, setDraft] = useState({ minValue: "", maxValue: "" });
  const [saving, setSaving] = useState(false);

  const reload = () => {
    apiFetch<QualityRuleDto[]>("/quality-rules")
      .then((r) => {
        setRules(r);
        setError(null);
      })
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load quality rules"));
  };

  useEffect(reload, []);

  const startEdit = (rule: QualityRuleDto) => {
    setEditingId(rule.id);
    setDraft({ minValue: String(rule.minValue), maxValue: String(rule.maxValue) });
    setError(null);
  };

  const cancelEdit = () => {
    setEditingId(null);
  };

  const saveEdit = async (id: number) => {
    setSaving(true);
    setError(null);
    try {
      await apiFetch(`/quality-rules/${id}`, {
        method: "PATCH",
        body: JSON.stringify({ minValue: Number(draft.minValue), maxValue: Number(draft.maxValue) }),
      });
      setEditingId(null);
      reload();
    } catch (err) {
      // The backend is the authority here - e.g. it rejects minValue >=
      // maxValue, and rejects editing a global rule unless the user has
      // allCentres access (quality-rules.service.ts). Surface exactly what
      // it says rather than re-deriving the same checks client-side.
      setError(err instanceof ApiError ? err.message : "Failed to update quality rule");
    } finally {
      setSaving(false);
    }
  };

  if (error && rules === null) return <p className="error">{error}</p>;
  if (rules === null) return <p>Loading...</p>;

  return (
    <div>
      <h1>Quality Rules</h1>
      <p className="hint">
        Acceptable range per parameter. A centre-specific rule overrides the global default for that centre.
      </p>
      {error && <p className="error">{error}</p>}

      {rules.length === 0 ? (
        <p className="hint">No quality rules are visible to your account.</p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Parameter</th>
              <th>Scope</th>
              <th>Min</th>
              <th>Max</th>
              {hasPermission(PERMISSIONS.QUALITY_RULE_CONFIGURE) && <th>Action</th>}
            </tr>
          </thead>
          <tbody>
            {rules.map((rule) => {
              const isEditing = editingId === rule.id;
              return (
                <tr key={rule.id}>
                  <td>{rule.parameter}</td>
                  <td>{rule.centreId === null ? "Global default" : `Centre ${rule.centreId}`}</td>
                  <td>
                    {isEditing ? (
                      <input
                        type="number"
                        step="0.01"
                        value={draft.minValue}
                        onChange={(e) => setDraft({ ...draft, minValue: e.target.value })}
                      />
                    ) : (
                      rule.minValue
                    )}
                  </td>
                  <td>
                    {isEditing ? (
                      <input
                        type="number"
                        step="0.01"
                        value={draft.maxValue}
                        onChange={(e) => setDraft({ ...draft, maxValue: e.target.value })}
                      />
                    ) : (
                      rule.maxValue
                    )}
                  </td>
                  {hasPermission(PERMISSIONS.QUALITY_RULE_CONFIGURE) && (
                    <td>
                      {isEditing ? (
                        <div className="override-controls">
                          <button onClick={() => saveEdit(rule.id)} disabled={saving}>
                            {saving ? "Saving..." : "Save"}
                          </button>
                          <button onClick={cancelEdit} disabled={saving}>
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <button onClick={() => startEdit(rule)}>Edit</button>
                      )}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
