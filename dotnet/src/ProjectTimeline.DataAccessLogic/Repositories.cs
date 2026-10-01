using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;
public interface IRepository<T> where T : AuditedEntity { IQueryable<T> Query(); Task<T?> GetAsync(Guid id, CancellationToken ct = default); Task AddAsync(T entity, CancellationToken ct = default); void Remove(T entity); }
public sealed class Repository<T>(ProjectTimelineDbContext db) : IRepository<T> where T : AuditedEntity { public IQueryable<T> Query() => db.Set<T>(); public Task<T?> GetAsync(Guid id, CancellationToken ct = default) => db.Set<T>().SingleOrDefaultAsync(x => x.Id == id, ct); public Task AddAsync(T entity, CancellationToken ct = default) => db.Set<T>().AddAsync(entity, ct).AsTask(); public void Remove(T entity) => db.Set<T>().Remove(entity); }
