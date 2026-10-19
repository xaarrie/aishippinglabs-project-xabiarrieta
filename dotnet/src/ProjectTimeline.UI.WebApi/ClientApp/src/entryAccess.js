export function hasEntryWriteAccess(user, project, participants = []) {
  return Boolean(user && project && ((user.roleCode || user.roleName) === 'Admin' || project.ownerId === user.id || participants.some(p => p.userId === user.id && ['Creator', 'Contributor'].includes(p.projectRoleName))));
}
