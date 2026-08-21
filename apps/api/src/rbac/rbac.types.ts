/**
 * The authenticated request-user shape attached to `req.user` by JwtStrategy.
 *
 * Permissions and centre access are loaded fresh from the database on every
 * request (see JwtStrategy.validate) rather than cached in the JWT or an
 * in-memory store. For the data volumes in this MVP (a handful of roles and
 * permissions), that is fast enough, and it means a permission or centre
 * assignment change takes effect on the user's very next request instead of
 * only at next login - no cache-invalidation mechanism needed (Rule 4:
 * don't build what isn't needed yet).
 */
export interface CentreAccess {
  allCentres: boolean;
  centreIds: number[];
}

export interface RequestUser {
  id: number;
  email: string;
  fullName: string;
  roleNames: string[];
  permissionCodes: string[];
  centreAccess: CentreAccess;
}
