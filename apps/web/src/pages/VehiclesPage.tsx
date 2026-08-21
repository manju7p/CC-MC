import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import type { ChillingCentreDto, VehicleDto } from "@cc-mc/shared-types";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

export function VehiclesPage() {
  const { hasPermission } = useAuth();
  const [vehicles, setVehicles] = useState<VehicleDto[]>([]);
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [form, setForm] = useState({ vehicleNumber: "", driverName: "", centreId: "" });

  const reload = () => {
    apiFetch<VehicleDto[]>("/vehicles").then(setVehicles).catch((err) => setError(err.message));
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
      await apiFetch("/vehicles", {
        method: "POST",
        body: JSON.stringify({
          vehicleNumber: form.vehicleNumber,
          driverName: form.driverName,
          centreId: Number(form.centreId),
        }),
      });
      setForm({ vehicleNumber: "", driverName: "", centreId: form.centreId });
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to create vehicle");
    }
  };

  return (
    <div>
      <h1>Vehicles</h1>
      {error && <p className="error">{error}</p>}
      <table className="data-table">
        <thead>
          <tr>
            <th>Vehicle number</th>
            <th>Driver</th>
            <th>Capacity (kg)</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {vehicles.map((v) => (
            <tr key={v.id}>
              <td>{v.vehicleNumber}</td>
              <td>{v.driverName ?? "-"}</td>
              <td>{v.capacityKg ?? "-"}</td>
              <td>{v.status}</td>
            </tr>
          ))}
        </tbody>
      </table>

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
          <button type="submit">Add vehicle</button>
        </form>
      )}
    </div>
  );
}
