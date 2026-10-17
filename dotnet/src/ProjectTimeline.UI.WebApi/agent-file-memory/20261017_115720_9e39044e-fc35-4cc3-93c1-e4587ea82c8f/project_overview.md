This document outlines the overarching view and guidelines for the Project Timeline system that is being developed.

### Objective
The Project Timeline system aims to facilitate a structured conversation around projects, allowing users to incrementally add and manage information over time. It should support various document types (PDF, Word, Excel, etc.) and includes features like snapshots for tracking changes.

### Database Specification
- **Database**: PostgreSQL
- **Connection Details**:
  - Server: localhost
  - User: postgres
  - Password: 1906As
  - Database Name: ibernovatimeline

### Key Components
1. **User Management**: Allows user registration, login, and password recovery.
2. **Project Management**: Users can create projects, invite participants, and manage different timelines.
3. **Timeline Management**: Users can add entries to timelines, manage snapshots, and conduct text-based searches within timeline content.
4. **UI Development**: React application with various screens for user and project management.

### Initial Roles
- Administrator
- Consultant
- User
- Chief

### Expected Features
- User roles with varying permissions
- Audit logs for changes and activities
- Review mechanisms for timeline merges 

### Documenting System Specifications
All tables must include creation timestamps and last updated timestamps, providing insights into data maintenance through time.

### Deployment Notes
- Ensure all initial records are created in the database.
- The application should be fully operational by executing `dotnet run`.