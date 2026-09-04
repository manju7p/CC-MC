import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import type { ChillingCentreDto, VehicleDto } from "@cc-mc/shared-types";
import { PERMISSIONS, RecordStatus } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

type EditDraft = {
  tankerNumber: string;
  driverName: string;
  driverMobile: string;
  capacityKg: string;
  status: RecordStatus;
};

function toDraft(vehicle: VehicleDto): EditDraft {
  return {
    tankerNumber: vehicle.tankerNumber ?? "",
    driverName: vehicle.driverName ?? "",
    driverMobile: vehicle.driverMobile ?? "",
    capacityKg: vehicle.capacityKg !== null ? String(vehicle.capacityKg) : "",
    status: vehicle.status,
  };
}

export function VehiclesPage() {
  const { hasPermission } = useAuth();
  const [vehicles, setVehicles] = useState<VehicleDto[]>([]);
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState({ vehicleNumber: "", driverName: "", centreId: "" });
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [draft, setDraft] = useState<EditDraft | null>(null);
  const [saving, setSaving] = useState(false);

  const reload = () => {
    apiFetch<VehicleDto[]>("/vehicles")
      .then((list) => {
        setVehicles(list);
        setError(null);
      })
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load vehicles"))
      .finally(() => setLoading(false));
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
    setCreating(true);
    try {
      await apiFetch("/vehicles", {
        method: "POST",
        body: JSON.stringify({
          vehicleNumber: form.vehicleNumber,
          driverName: form.driverName || undefined,
          centreId: Number(form.centreId),
        }),
      });
      setForm({ vehicleNumber: "", driverName: "", centreId: form.centreId });
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to create vehicle");
    } finally {
      setCreating(false);
    }
  };

  const startEdit = (vehicle: VehicleDto) => {
    setEditingId(vehicle.id);
    setDraft(toDraft(vehicle));
    setError(null);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setDraft(null);
  };

  const saveEdit = async (id: number) => {
    if (!draft) return;
    setSaving(true);
    setError(null);
    try {
      await apiFetch(`/vehicles/${id}`, {
        method: "PATCH",
        body: JSON.stringify({
          tankerNumber: draft.tankerNumber || undefined,
          driverName: draft.driverName || undefined,
          driverMobile: draft.driverMobile || undefined,
          capacityKg: draft.capacityKg ? Number(draft.capacityKg) : undefined,
          status: draft.status,
        }),
      });
      setEditingId(null);
      setDraft(null);
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to update vehicle");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div>
      <h1>Vehicles</h1>
      {error && <p className="error">{error}</p>}
      {loading ? (
        <p>Loading...</p>
      ) : vehicles.length === 0 ? (
        <p className="hint">No vehicles yet.</p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Vehicle number</th>
              <th>Tanker #</th>
              <th>Driver</th>
              <th>Driver mobile</th>
              <th>Capacity (kg)</th>
              <th>Status</th>
              {hasPermission(PERMISSIONS.VEHICLE_EDIT) && <th>Action</th>}
            </tr>
          </thead>
          <tbody>
            {vehicles.map((v) => {
              const editingRow: EditDraft | null = editingId === v.id ? draft : null;
              return (
                <tr key={v.id}>
                  <td>{v.vehicleNumber}</td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.tankerNumber}
                        onChange={(e) => setDraft({ ...editingRow, tankerNumber: e.target.value })}
                      />
                    ) : (
                      v.tankerNumber ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.driverName}
                        onChange={(e) => setDraft({ ...editingRow, driverName: e.target.value })}
                      />
                    ) : (
                      v.driverName ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.driverMobile}
                        onChange={(e) => setDraft({ ...editingRow, driverMobile: e.target.value })}
                      />
                    ) : (
                      v.driverMobile ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        type="number"
                        step="0.01"
                        value={editingRow.capacityKg}
                        onChange={(e) => setDraft({ ...editingRow, capacityKg: e.target.value })}
                      />
                    ) : (
                      v.capacityKg ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <select
                        value={editingRow.status}
                        onChange={(e) => setDraft({ ...editingRow, status: e.target.value as RecordStatus })}
                      >
                        <option value={RecordStatus.ACTIVE}>ACTIVE</option>
                        <option value={RecordStatus.INACTIVE}>INACTIVE</option>
                      </select>
                    ) : (
                      v.status
                    )}
                  </td>
                  {hasPermission(PERMISSIONS.VEHICLE_EDIT) && (
                    <td>
                      {editingRow ? (
                        <div className="override-controls">
                          <button onClick={() => saveEdit(v.id)} disabled={saving}>
                            {saving ? "Saving..." : "Save"}
                          </button>
                          <button onClick={cancelEdit} disabled={saving}>
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <button onClick={() => startEdit(v)}>Edit</button>
                      )}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      {hasPermission(PERMISSIONS.VEHICLE_CREATE) && (
        <form onSubmit={handleCreate} className="form inline-form">
          <h2>Add vehicle</h2>
          <label>
            Vehicle number
            <input
              value={form.vehicleNumber}
              onChange={(e) => setForm({ ...form, vehicleNumber: e.target.value })}
              required
            />
          </label>
          <label>
            Driver name
            <input value={form.driverName} onChange={(e) => setForm({ ...form, driverName: e.target.value })} />
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
          <button type="submit" disabled={creating}>
            {creating ? "Adding..." : "Add vehicle"}
          </button>
        </form>
      )}
    </div>
  );
}
