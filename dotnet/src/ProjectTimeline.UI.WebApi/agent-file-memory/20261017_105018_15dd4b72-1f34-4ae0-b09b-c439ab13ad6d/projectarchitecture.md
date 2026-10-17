# Project Timeline Specification

## Description of the Application
It manages a conversation about a project or a topic that includes incremental information over time. For example, during progress on a project, a transcript can be added in one meeting, and in another, a document can be added. It is crucial to be able to navigate through documents but also create artifacts like snapshots of the project lifecycle to identify any incongruities between documents and information. This allows the uploader to resolve or complete these items. The artifacts also demonstrate how the conversation evolves over time as objectives and specifications can change.

## Database Specification
- **DBMS**: PostgreSQL  
- **Development Connection String**:  
  - `server:localhost`  
  - `user:postgres`  
  - `password:1906As`  
  - `database name: ibernovatimeline`

## Tables
All tables must have their creation and last update timestamp fields populated by the application.

### User Table
- **IDUser**: int  
- **Email**: string (max 100 characters)  
- **Password**  
- **Name**: string (max 100 characters)  
- **IDUserRole**: int  
**Existing Users**:  
- Name: admin  
  - Email: x.arrieta@ibernova.com  
  - Role: Administrator

### User Role Table
The different roles the application can accommodate. Initially, it will distinguish if a user is an admin or not.
- **IDUserRole**: int  
- **Code**: string (max 20 characters)  
- **Description**: string (max 250 characters)  
- **IsAdmin**: bit (true represents admin rights)
**Existing User Roles**:
- Administrator (has admin rights)  
- Consultant  
- User  
- Chief

### Project Table
A unit of discussion that can have a description and goals. It's created by a user, either admin or non-admin, and can include other participants visible to the project.  
- **IDProject**: int  
- **Name**: string (max 100 characters)  
- **Description**: text  
- **IDMainTimeline**: int  
- **IDUser**: int (user that created the Project)  
- **IDUserRole**: int (always the creator)
**Project Participant Table**: Participants are added by the Project creator.
- **IDProjectParticipant**  
- **IDProject**  
- **IDUser**  
- **IDProjectRole**
**Project Role Table**: The different roles for a user in a Project, changeable upon participant addition.
- **IDProjectRole**: int  
- **Code**: string (max 20 characters)  
- **Description**: string (max 250 characters)

### Timeline Table
A timeline serves as a conversation for the Project where various entries can be submitted and maintained.
- **IDTimeline**: int  
- **IDProject**: int  
- **CreationDateTime**: Date and Time of Creation

### TimeLine Entry Table
Entries comprise different artifacts uploaded, which can include PDFs, Word documents, videos, audio files, or text. Artifacts should be accessible if not included as mere text.
- **IDTimeLineEntry**: int  
- **IDTimeLine**: int  
- **EntryNumber**: int  
- **CreationDateTime**: Date and Time of Creation  
- **Content**: text

### Technology Considerations
- **Framework**: .NET 10  
- **AI Operations**: Microsoft Agent Framework with Harness  
- **ORM**: Entity Framework Core
- **Database**: PostgreSQL  
- **UI Framework**: React  

## Tasks Needed
1. **Implement Timeline Entry Classes**  
2. **Create Maintenance Screens**  
3. **Implement Search Functions**  
4. **Deploy Database with Initial Records**  

All components must run via `dotnet run`. Tasks should be organized into development and testing phases.