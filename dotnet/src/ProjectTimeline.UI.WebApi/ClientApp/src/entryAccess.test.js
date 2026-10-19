import test from 'node:test';
import assert from 'node:assert/strict';
import { hasEntryWriteAccess } from './entryAccess.js';

test('retained entry access preserves owner, admin, contributor and creator access', () => {
  const user = { id: 'u', roleCode: 'User' }, project = { ownerId: 'owner' };
  assert.equal(hasEntryWriteAccess(user, { ownerId: 'u' }), true);
  assert.equal(hasEntryWriteAccess({ ...user, roleCode: 'Admin' }, project), true);
  assert.equal(hasEntryWriteAccess({ id: 'u', roleName: 'Admin' }, project), true);
  for (const role of ['Contributor', 'Creator']) assert.equal(hasEntryWriteAccess(user, project, [{ userId: 'u', projectRoleName: role }]), true);
  for (const participants of [[], [{ userId: 'u', projectRoleName: 'Reviewer' }], [{ userId: 'other', projectRoleName: 'Contributor' }]]) assert.equal(hasEntryWriteAccess(user, project, participants), false);
  assert.equal(hasEntryWriteAccess(null, project), false);
  assert.equal(hasEntryWriteAccess(user, null), false);
});
