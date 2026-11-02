# Follow-up: initial script stopped on comparing responseEvidence to the full response.
# SearchJudgeInput.cs:29-32 intentionally excludes content; answer is compared separately.
# Existing derived CSV and corrected-label snapshot are read/validated, never overwritten.
from pathlib import Path
from datetime import datetime,timezone
import csv,io,json,hashlib,copy,collections
OUT=Path('C:/Data/formacion/TimelineSpecifications/search-agent-prompt-iteration-20261102T231101Z-f1468fb6')
S=Path('C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def save(name,obj):
 with (OUT/name).open('x',encoding='utf-8') as f:json.dump(obj,f,indent=2,ensure_ascii=False)
def writecsv(name,header,rows):
 if (OUT/name).exists():
  with (OUT/name).open(encoding='utf-8',newline='') as f:assert list(csv.reader(f,strict=True))==[header]+rows
  return
 with (OUT/name).open('x',encoding='utf-8',newline='') as f:
  w=csv.writer(f,quoting=csv.QUOTE_ALL,lineterminator='\r\n');w.writerow(header);w.writerows(rows)
def decode(s):return s[1:] if s.startswith("'") else s
source=Path(str(S)+'.jsonl');labels_path=Path(str(S)+'.labels.csv');base=Path(str(S)+'.judge-20261102T2237495800801Z')
SH='e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2';LH='a704ba82d039461ac6d76a71aea1beeaa42f72dcdb2502e416257ef5da23aabe'
assert sha(source)==SH and sha(labels_path)==LH
records=[json.loads(x) for x in source.read_text(encoding='utf-8-sig').splitlines()];assert len(records)==50
identity=lambda r:(r['runId'],r['scenarioId'])
byid={identity(r):r for r in records};assert len(byid)==50
with labels_path.open(encoding='utf-8-sig',newline='') as f:lr=list(csv.reader(f,strict=True))
header=lr[0];labels=lr[1:];assert header==['version','datasetSha256','runId','scenarioId','ordinal','question','label','failureCategory','revision','updatedUtc']
assert len(labels)==17 and all(len(r)==10 for r in labels)
scope={'actorId':'0e6047b4-385d-4d7c-bbcf-4926e7c8fc70','timelineId':'76ba87c5-71a4-4bf6-a87b-d1a40a3ec86c','projectId':'8d3559af-77d8-476d-b763-5f085889861d'}
selected=[];seen=set()
for l in labels:
 key=(decode(l[2]),decode(l[3]));assert key not in seen;seen.add(key);r=byid[key]
 assert l[0]=='1' and l[1]==SH and l[4]==str(r['scenario']['ordinal']) and decode(l[5])==r['scenario']['question']
 assert l[6] in ['Good','Bad'] and int(l[8])>=1 and all(r['scope'][k]==v for k,v in scope.items())
 selected.append(r)
assert [r['scenario']['ordinal'] for r in selected]==list(range(1,18))
manifest=json.loads(Path(str(S)+'.jsonl.manifest.json').read_text(encoding='utf-8'));original=Path(manifest['inputPath']);assert sha(original)==manifest['inputSha256'].lower()
with original.open(encoding='utf-8-sig',newline='') as f:orig=list(csv.reader(f,strict=True))
assert len(orig)==51
headers=[x['header'] for x in selected[0]['scenario']['fields']];meta=['sourceRunId','sourceScenarioId','sourceOrdinal','sourceInputSha256','sourceDatasetSha256']
assert len({h.casefold() for h in headers+meta})==len(headers+meta)
newrows=[];mapping=[]
for n,r in enumerate(selected,1):
 fields=r['scenario']['fields'];values=[x['value'] for x in fields]
 assert [x['header'] for x in fields]==headers==orig[0] and values==orig[r['scenario']['ordinal']]
 assert r['inputSha256'].lower()==manifest['inputSha256'].lower()
 metadata=[r['runId'],r['scenarioId'],str(r['scenario']['ordinal']),r['inputSha256'],SH]
 newrows.append(values+metadata);mapping.append({'derivedOrdinal':n,**dict(zip(meta,metadata)),'question':r['scenario']['question'],'orderedSourceFields':fields,'scope':scope})
writecsv('derived-17.csv',headers+meta,newrows)
with (OUT/'derived-17.csv').open(encoding='utf-8',newline='') as f:roundtrip=list(csv.reader(f,strict=True))
assert len(roundtrip)==18 and roundtrip==[headers+meta]+newrows
for r,row in zip(selected,roundtrip[1:],strict=True):
 assert row[headers.index('question')]==r['scenario']['question']
 assert list(zip(headers,row[:len(headers)]))==[(x['header'],x['value']) for x in r['scenario']['fields']]
with (OUT/'corrected-OLD-labels.csv').open(encoding='utf-8',newline='') as f:now=list(csv.reader(f,strict=True))[1][9]
corrected=copy.deepcopy(labels);changes=[]
for old,new in zip(labels,corrected,strict=True):
 if int(old[4]) in [1,3,17]:
  assert old[6]=='Good';new[6]='Bad';new[7]='incorrect-answer';new[8]=str(int(old[8])+1);new[9]=now
  changes.append({'sourceOrdinal':int(old[4]),'reason':'User-confirmed OLD-answer wrong refusal; not prompt improvement','fields':{header[i]:{'before':old[i],'after':new[i]} for i in range(10) if old[i]!=new[i]}})
 else:assert old==new
assert len(changes)==3
writecsv('corrected-OLD-labels.csv',header,corrected)
with (OUT/'corrected-OLD-labels.csv').open(encoding='utf-8',newline='') as f:assert list(csv.reader(f,strict=True))==[header]+corrected
predictions=[json.loads(x) for x in (base/'predictions.jsonl').read_text(encoding='utf-8').splitlines()];assert len(predictions)==17
pm={(p['identity']['runId'],p['identity']['scenarioId']):p for p in predictions};assert len(pm)==17
counts=collections.Counter();joined=[]
for l,r in zip(corrected,selected,strict=True):
 p=pm[identity(r)];assert p['status']=='Success' and p['identity']['datasetSha256']==SH and p['identity']['ordinal']==r['scenario']['ordinal']
 assert p['question']==r['scenario']['question'] and p['answer']==r['response']['content'] and p['responseEvidence']=={k:v for k,v in r['response'].items() if k!='content'}
 q={('Bad','bad'):'TP',('Good','bad'):'FP',('Bad','good'):'FN',('Good','good'):'TN'}[(l[6],p['judgment']['label'])];counts[q]+=1
 joined.append({'identity':p['identity'],'question':p['question'],'oldAnswer':p['answer'],'correctedOldHumanLabel':l[6],'fixedHistoricalJudgment':p['judgment'],'confusion':q})
assert counts=={'TP':6,'FP':3,'FN':2,'TN':6}
metrics={'evaluationKind':'Offline recomputation: corrected OLD labels and unchanged historical predictions; no new judge calls','totalSource':50,'totalLabels':17,'success':17,'errors':0,**dict(counts),'accuracy':12/17,'precisionBad':6/9,'recallBad':6/8,'labelCoverage':17/50,'predictionCoverageOfLabeled':1.0,'predictedBad':9,'predictedGood':8,'notPromptImprovement':True}
save('corrected-baseline.json',{'metrics':metrics,'rows':joined})
save('provenance.json',{'createdUtc':now,'originalSource':str(source),'sourceDatasetSha256':SH,'originalLabels':str(labels_path),'originalLabelsSha256':LH,'historicalScenarioCsv':str(original),'historicalScenarioCsvSha256':sha(original),'historicalPredictions':str(base/'predictions.jsonl'),'historicalPredictionsSha256':sha(base/'predictions.jsonl'),'derivedCsvSha256':sha(OUT/'derived-17.csv'),'sourceRows':50,'selectedRows':17,'roundtrip':'Python strict CSV parse, all questions/ordered original fields exactly preserved; not production .NET parser execution','metadataHeaders':meta,'corrections':changes,'correctedOldLabelsSha256':sha(OUT/'corrected-OLD-labels.csv'),'categoryChoice':'incorrect-answer is documented and consistent with prior wrong-refusal labels','mapping':mapping})
assert sha(source)==SH and sha(labels_path)==LH
print(json.dumps({'selectedRows':17,'originalRows':50,'exactQuestionAndFieldRoundtrip':True,'metadataCollisionChecked':True,'correctedOldMetrics':metrics},indent=2))
