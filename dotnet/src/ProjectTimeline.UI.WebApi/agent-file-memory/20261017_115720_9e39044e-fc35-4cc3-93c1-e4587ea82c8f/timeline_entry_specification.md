Implementa las siguientes tablas con su funcionalidad

TimeLineEntry
Esta clase representa las entradas del timeline es el contenido generado a partir de la inclusion de ficheros
pero de comento haremos que el contenido sea metido a mano
IDTimeLineEntry int
IDTimeLine int
Content text
EntryDate: fecha y hora actual de la entrada.

TimeLineSnapshot
Es el snapshot de un timeline que se ha generado a partir de una serie de entrada
IDTimeLineSnapshot int
Snapshot number -> Es el numero de snapshot a nivel de timeline
IDTimeLine int
SnapshotDate: fecha y hora del snapshot
Content text es el resumen de todas las entradas del snapshot

TimeLineSnapshotEntries
Son el conjunto de entradas que han generado el snapshot
IDTimeLineSnapshotEntries int
IDTimeLineSnapshot int
IDTineLineEntry int

Debes crear la pantalla de mantenimiento de las entradas del timeline.
Además quiero que implementes las funciones de busquedas definidas para las entradas en la siguiente clase
C:\Data\formacion\aishippinglabs-project-xabiarrieta\dotnet\src\ProjectTimeline.BusinessLogic\Agents\SearchTools.cs

Aqui hay dos, aunque estan definidas como void 
fetch_entry -> devuelve el contenido de una entrada
search_entries -> hace una busqueda de texto por contenido semantico, sin vectores, busca si se puede hacer full text
search en postgree.
Como parametros deberá recibir de momento solo el texto a buscar y el idtimeline actual
Esta busqueda se implementará en la pantalla de mantenimiento del timeline donde con una caja de texto devolveremos
el contenido de las entradas que coincidan concatenadas.