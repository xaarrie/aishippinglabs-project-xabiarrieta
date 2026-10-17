# Project Timeline Specification
## Implementation Details
- Implement the **TimeLineEntry** class with the following properties:
  - `IDTimeLineEntry`: int  
  - `IDTimeLine`: int  
  - `Content`: text  
  - `EntryDate`: date and time of the entry  

- Implement the **TimeLineSnapshot** class with the following properties:
  - `IDTimeLineSnapshot`: int  
  - `SnapshotNumber`: int  
  - `IDTimeLine`: int  
  - `SnapshotDate`: date and time of the snapshot  
  - `Content`: summary of all entries in the snapshot  

- Implement the **TimeLineSnapshotEntries** class with:
  - `IDTimeLineSnapshotEntries`: int  
  - `IDTimeLineSnapshot`: int  
  - `IDTimeLineEntry`: int  

## Steps for Implementation
1. **Create Maintenance Screen**: This should allow users to manage timeline entries easily.
2. **Implement search functions in SearchTools.cs**
   - **fetch_entry**: Retrieves the content of an entry.
   - **search_entries**: Conducts a text search on semantic content, enabling full-text search in PostgreSQL.
3. **Testing**: Ensure these functionalities work correctly across various scenarios.