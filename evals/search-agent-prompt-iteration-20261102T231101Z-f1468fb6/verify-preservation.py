from pathlib import Path
import hashlib,re,json,subprocess,os
OUT=Path('C:/Data/formacion/TimelineSpecifications/search-agent-prompt-iteration-20261102T231101Z-f1468fb6');REPO=Path.cwd()
WORKER=Path('C:/Users/biarunxa/.gentle-shell/agent/gentle-agents/sessions/2026-11-02T23-00-45-475Z_01a19e7e-6322-718e-a00f-7ae0b655953d.jsonl')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def save(name,x):
 with (OUT/name).open('x',encoding='utf-8') as f:json.dump(x,f,indent=2,ensure_ascii=False)
wl=WORKER.read_text(encoding='utf-8').splitlines()
def wt(n):return '\n'.join(c.get('text','') for c in json.loads(wl[n-1])['message']['content'])
agent=REPO/'dotnet/src/ProjectTimeline.BusinessLogic/Agents/SearchEntriesAgent.cs';test=REPO/'dotnet/tests/ProjectTimeline.Tests/SearchEntriesPromptContractTests.cs'
masked,n=re.subn(rb'(    private const string (?:PlanningInstructions|SynthesisInstructions) = """\r?\n)(.*?)(        """;)',lambda m:m[1]+b'<PROMPT_CONTENT>'+m[3],agent.read_bytes(),flags=re.S)
h=hashlib.sha256(masked).hexdigest();assert n==2 and h=='87a6d7b28011e2de44d46daff4d0a055a5d508f9c6328ba0fc880ffd86323688'
assert wt(35).count('Assert.Contains() Failure')==15 and '15, Superado:    82' in wt(35) and 'Superado:    97' in wt(41)
writes=[c['arguments']['content'] for x in wl for c in json.loads(x).get('message',{}).get('content',[]) if c.get('type')=='toolCall' and c.get('name')=='write' and c.get('arguments',{}).get('path')=='dotnet/tests/ProjectTimeline.Tests/SearchEntriesPromptContractTests.cs']
assert len(writes)==1 and test.read_bytes()==writes[0].encode('utf-8')
status=subprocess.run(['git','status','--short'],capture_output=True,text=True,check=True).stdout.strip();old=wt(48);old=old[old.index(' M .atl/'):].replace('\r','').strip();assert status==old
S=Path('C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311')
rubrics=[Path(str(S)+'.judge-20261102T2237495800801Z/rubric.md'),REPO/'dotnet/src/ProjectTimeline.SearchFactsJudge/Prompts/SearchResponseJudge.md']+list((REPO/'dotnet/tests/ProjectTimeline.Tests/bin').rglob('SearchResponseJudge.md'))
rh='4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992';assert len(rubrics)==6 and all(sha(p)==rh for p in rubrics)
assert sha(Path(str(S)+'.jsonl'))=='e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2'
assert sha(Path(str(S)+'.labels.csv'))=='a704ba82d039461ac6d76a71aea1beeaa42f72dcdb2502e416257ef5da23aabe'
judge_files={str(p):sha(p) for p in (REPO/'dotnet/src/ProjectTimeline.SearchFactsJudge').rglob('*') if p.is_file() and not {'bin','obj'}&set(p.relative_to(REPO/'dotnet/src/ProjectTimeline.SearchFactsJudge').parts)}
proof={'maskedSearchSourceSha256':h,'fullSearchSourceSha256':sha(agent),'testSourceSha256':sha(test),'testMatchesWorkerWrittenBytes':True,'gitStatusMatchesWorkerFinal':True,'rubrics':[{'path':str(p),'sha256':sha(p)} for p in rubrics],'judgeFilesCurrentHashes':judge_files,'attributionLimit':'Masked source bytes and exact worker-authored test independently verified; ambient worktree not attributed to S1. Judge entire-source historical snapshot not supplied; rubric historical bytes independently verified.','workerEvidence':{'path':str(WORKER),'baselineLine':27,'redLine':35,'redContainsAssertionFailures':15,'redExistingPasses':82,'greenLine':41,'greenPasses':97,'preservationLine':48}}
save('source-preservation.json',proof)
launch=json.loads((REPO/'dotnet/src/ProjectTimeline.UI.WebApi/Properties/launchSettings.json').read_text(encoding='utf-8-sig'));envs=[v.get('environmentVariables',{}) for v in launch.get('profiles',{}).values()]
credentials={'processConnectionStrings__ProjectTimelinePresent':bool(os.environ.get('ConnectionStrings__ProjectTimeline','').strip()),'processOPENAI_API_KEYPresent':bool(os.environ.get('OPENAI_API_KEY','').strip()),'launchSettingsDistinctOpenAIKeyCount':len({e['OPENAI_API_KEY'] for e in envs if isinstance(e.get('OPENAI_API_KEY'),str) and e['OPENAI_API_KEY'].strip()}),'launchSettingsHasMatchingDatabasePropertyName':any('ConnectionStrings__ProjectTimeline' in e for e in envs)}
assert credentials=={'processConnectionStrings__ProjectTimelinePresent':False,'processOPENAI_API_KEYPresent':False,'launchSettingsDistinctOpenAIKeyCount':1,'launchSettingsHasMatchingDatabasePropertyName':False}
save('credential-boundary.json',{'observed':credentials,'preflightExecuted':False,'reason':'Required launching-process DB connection absent; no other database secret source authorized.','environmentMutated':False,'secretsStoredOrLogged':False,'paidCalls':0})
# Preserve exact command evidence without copying unrelated parent session content.
session=Path(os.environ['PI_SESSION_FILE']);observed=[]
for line_no,line in enumerate(session.read_text(encoding='utf-8').splitlines(),1):
 x=json.loads(line);m=x.get('message',{})
 for c in m.get('content',[]):
  if c.get('type')=='toolCall':
   args=c.get('arguments',{});cmd=args.get('command','')
   if cmd.startswith(('dotnet test ','dotnet build ','git diff --check ')):observed.append({'sessionLine':line_no,'command':cmd})
  elif m.get('role')=='toolResult' and c.get('type')=='text':
   t=c.get('text','')
   if any(w in t for w in ['Superado:   167','Compilación correcta.']):observed.append({'sessionLine':line_no,'observedOutput':t})
save('commands-observed.json',{'sessionPath':str(session),'commandsAndResults':observed,'otherInspectionCommands':'Read-only git diff/status and Python inspections are retained in this verifier session. Offline generation/verification scripts in this directory are reproducible; no extra test/build/lint commands executed.','artifactPreparationFailures':['First long inline preparation command failed SyntaxError before any mutation.','prepare-offline.py stopped after creating derived/corrected CSVs: responseEvidence deliberately omits content, unlike full response. SearchJudgeInput.cs confirmed this projection. Followup compares answer separately and excludes content; original failed script and partial artifacts retained, never overwritten.']})
print(json.dumps({'maskedSourceMatchesWorkerBaseline':True,'testMatchesWorkerBytes':True,'workerRedFailures':15,'workerExistingPasses':82,'workerGreen':97,'allSixRubricsMatch':True,'worktreeStatusMatchesWorkerFinal':True,'credentialBooleans':credentials},indent=2))
