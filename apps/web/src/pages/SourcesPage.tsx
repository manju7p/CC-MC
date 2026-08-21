import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import type { ChillingCentreDto, SourceDto } from "@cc-mc/shared-types";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

export function SourcesPage() {
  const { hasPermission } = useAuth();
  const [sources, setSources] = useState<SourceDto[]>([]);
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [form, setForm] = useState({ code: "", name: "", centreId: "" });

  const reload = () => {
    apiFetch<SourceDto[]>("/sources").then(setSources).catch((err) => setError(err.message));
  };

  useEffect(() => {
    reload();
    apiFetch<ChillingCentreDto[]>("/centres").then((list) => {
      setCentres(list);
      if (list.length === 1) setForm((f) => ({ ...f, centreId: String(list[0].id) }));
    });
  }, []);

  const handleCreate = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await apiFetch("/sources", {
        method: "POST",
        body: JSON.stringify({ code: form.code, name: form.name, centreId: Number(form.centreId) }),
      });
      setForm({ code: "", name: "", centreId: form.centreId });
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to create source");
    }
  };

  return (
    <div>
      <h1>Sources</h1>
      {error && <p className="error">{error}</p>}
      <table className="data-table">
        <thead>
          <tr>
            <th>Code</th>
            <th>Name</th>
            <th>Milk type</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {sources.map((s) => (
            <tr key={s.id}>
              <td>{s.code}</td>
              <td>{s.name}</td>
              <td>{s.milkType ?? "-"}</td>
              <td>{s.status}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {hasPermission(PERMISSIONS.SOURCE_CREATE) && (
        <form onSubmit={handleCreate} className="form inline-form">
          <h2>Add source</h2>
          <label>
            Code
            <input value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required />
          </label>
          <label>
            Name
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </label>
          {centres.length > 1 && (
            <label>
              Centre
              <select value={form.centreId} onChange={(e) => setForm({ ...form, centreId: e.target.value })} required>
                <option value="">Select centre</option>
                {centres.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button type="submit">Add source</button>
        </form>
      )}
    </div>
  );
}
