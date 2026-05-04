# RusticalandOPS API Refactoring - Complete Plan

## Document Index

Start here and follow the reading order based on your role:

### For Executives & Decision Makers
👉 **Start with**: `REFACTORING_EXECUTIVE_SUMMARY.md`
- Overview of current problems
- Proposed solution at high level
- Timeline and resource requirements
- Risk assessment
- Return on investment analysis
- Decision framework
- **Reading time**: 15 minutes
- **Decision needed**: Approve/defer/modify

### For Architects & Tech Leads
👉 **Then read**: `ARCHITECTURE_ANALYSIS.md`
- Complete current state analysis
- 12 identified responsibility domains with LOC counts
- Technical debt categorized by severity
- Proposed folder structure
- 30+ service interfaces with signatures
- Complete dependency injection graph
- Phase breakdown strategy
- Exception hierarchy
- Technical decisions & rationale
- **Reading time**: 45 minutes
- **Action**: Validate approach, refine if needed

### For Implementation Teams
👉 **Then read**: `ARCHITECTURE_IMPLEMENTATION_CHECKLIST.md`
- Phase-by-phase breakdown (7 phases over 8 weeks)
- Specific file creation tasks with folder structure
- Model extraction with line numbers from current code
- Service creation with what each service owns
- Endpoint refactoring details
- Test requirements per phase
- Quality gates before each phase
- Risk mitigation strategies
- Success metrics
- **Reading time**: 30 minutes
- **Action**: Use as primary implementation guide

### For Development Work (Phase 1 Specifically)
👉 **Then read**: `PHASE1_INFRASTRUCTURE_EXAMPLE.md`
- Real before/after code examples
- Complete utility class implementations
- Complete ConfigurationProvider implementation
- Complete DI container setup
- Complete middleware implementations
- Updated Program.cs example
- Integration test template
- Phase 1 verification checklist
- **Reading time**: 30 minutes
- **Action**: Follow as template for Phase 1 work

---

## Quick Reference

### Current State
- **File Size**: 6,863 lines
- **Endpoints**: 69 (GET/POST/PUT/DELETE)
- **Static Methods**: 100+
- **Inline Classes**: 50+
- **Testability**: ~0% unit testable
- **Maintainability**: Critical issues

### Target State
- **File Size**: <200 lines
- **Service Interfaces**: 50+
- **Endpoint Handlers**: 20+
- **Testability**: 80%+ coverage
- **Maintainability**: Clean, modular, extensible

### Timeline
| Phase | Focus | Duration | Team |
|-------|-------|----------|------|
| 1 | Infrastructure | 1 week | 1 engineer |
| 2-3 | Core Services | 2 weeks | 1-2 engineers |
| 4-5 | Business Services | 2 weeks | 2 engineers |
| 6 | Endpoint Refactoring | 1 week | 2 engineers |
| 7 | Cleanup | 1 week | 1 engineer |
| 8 | Testing & Rollout | 1 week | 2-3 people |
| **Total** | **All Phases** | **8 weeks** | **2-3 engineers** |

### Key Responsibilities

#### RCON Broker
- Persistent connection pooling
- Credential fallback chain
- Command execution and response parsing

#### API Gateway  
- 69 endpoints for server/agent/dashboard management
- HTTP routing and middleware
- Request/response serialization

#### Orchestration Layer
- Server lifecycle operations
- Remote agent coordination
- Health monitoring

#### Configuration Manager
- Server config loading/saving
- LLM settings management
- Environment variable handling
- Log rules configuration

#### Monitoring Service
- Dashboard data aggregation
- Service health tracking
- Network statistics
- Process monitoring

#### Agent Coordination
- Agent registration
- Request proxying to remote agents
- NeoCortex data parsing

#### Plugin System
- Oxide plugin validation
- Plugin metadata extraction
- Plugin installation

---

## FAQ

### Q: Will this break anything?
**A**: No. We preserve 100% API compatibility. This is internal refactoring only.

### Q: Can we do this faster?
**A**: Yes, with more engineers. Add 1 engineer = ~1 week faster. Add 2 engineers = ~2 weeks faster.

### Q: Can we do this slower?
**A**: Yes. Focus on Phase 1-3 first (~3 weeks), leave Phase 4-8 for later.

### Q: Can we skip this refactoring?
**A**: Technically yes, but code decay will accelerate. Technical debt will compound. New features become harder to add.

### Q: What if we find bugs during refactoring?
**A**: Good! That's why we test. Fix them in the new code.

### Q: Can we run old and new code in parallel?
**A**: Yes! Phase 1-5 can use feature flags to run both implementations. Phase 6-8 fully switches over.

### Q: What if we need to roll back?
**A**: Keep the pre-refactor branch tagged. Rolling back takes <1 hour.

### Q: Who should review the code?
**A**: Tech lead should review each phase. QA should regression test. Product team should do smoke tests.

### Q: How do we measure success?
**A**: Run load tests before/after. Compare dashboard generation time, endpoint latency, memory usage. Check test coverage metrics.

---

## Checklist for Getting Started

- [ ] **Decision Made** - Executive approval received
- [ ] **Resource Allocated** - 2-3 engineers confirmed for 8 weeks
- [ ] **Branch Created** - `git checkout -b refactor/modular-architecture`
- [ ] **Tickets Created** - Jira/GitHub issues for each phase
- [ ] **Review Process** - Code review templates prepared
- [ ] **Testing Plan** - Load testing environment ready
- [ ] **Monitoring** - Alerting configured for regressions
- [ ] **Communication** - Team briefed on timeline and expectations

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                        Program.cs (100 LOC)                 │
│                    (Bootstrap + Route registration)          │
└────┬────────────┬────────────┬──────────────┬───────────────┘
     │            │            │              │
┌────▼──┐  ┌─────▼──────┐ ┌──▼──────────┐ ┌─▼──────────┐
│Middleware       │Extensions  │Endpoints      │Middleware │
├────────┤ ├────────────┤ ├──────────────┤ ├────────────┤
│Error    │ │ServiceColl │ │ServerEndpoints│ │Error Handling
│Request  │ │Extensions  │ │RemoteServer   │ │Request Logging
│Logging  │ │AppBuilder  │ │Dashboard      │ │Auth Validation
│Auth     │ │Extensions  │ │Agent          │ └────────────┘
└────────┘ └─────────┬──┘ │Tasks          │
                     │    └────┬──────────┘
              ┌──────▼────────▼──────────┐
              │    Dependency Injection  │
              │      Container (DI)      │
              └──────┬───────┬────┬──────┘
                     │       │    │
        ┌────────────▼─┐  ┌─▼────▼──────┐  ┌──────────────┐
        │   Services   │  │Infrastructure│  │   Models     │
        ├──────────────┤  ├──────────────┤  ├──────────────┤
        │Servers       │  │Config        │  │Requests      │
        │RemoteServers │  │FileStorage   │  │Responses     │
        │Dashboard     │  │ProcessExec   │  │Dashboard DTOs│
        │Host Monitor  │  │Rcon Manager  │  │Shared Types  │
        │LLM           │  │JSON Persist  │  └──────────────┘
        │Plugins       │  │Http Clients  │
        │...           │  └──────────────┘
        └──────────────┘
        
        ┌──────────────┐
        │   Utilities  │
        ├──────────────┤
        │JsonUtils     │
        │StringUtils   │
        │PathUtils     │
        │DateUtils     │
        │Validation    │
        │Network       │
        │PluginParser  │
        └──────────────┘
```

---

## Phase 1 Quick Start

**If you're starting Phase 1, follow this path:**

1. Read `PHASE1_INFRASTRUCTURE_EXAMPLE.md`
2. Create folder structure:
   ```
   /Middleware
   /Services (empty for now)
   /Endpoints (empty for now)
   /Infrastructure/Configuration
   /Models/Requests
   /Models/Responses
   /Models/Dashboard
   /Models/Shared
   /Utilities
   /Extensions
   /BackgroundServices
   /Exceptions
   ```
3. Extract utilities to `/Utilities` (copy from examples)
4. Create models in `/Models` (copy from Program.cs)
5. Create DI container in `/Extensions`
6. Create middleware in `/Middleware`
7. Update Program.cs (use example)
8. Run tests - should all pass with no functional changes

**Estimated Time**: 3-5 days

---

## Architecture Principles

### 1. Single Responsibility Principle
Each service has one reason to change. A server service doesn't handle networking.

### 2. Dependency Injection
All services use constructor injection. Nothing is static except utilities.

### 3. Interface Segregation  
Large interfaces split into smaller, focused ones. Services implement only needed interfaces.

### 4. Composition Over Inheritance
Services are composed, not inherited. No base classes except exceptions.

### 5. Explicit Dependencies
All dependencies declared in constructor. No hidden globals.

### 6. Async All The Way
All I/O operations are async. Cancellation tokens propagated throughout.

### 7. Error Handling First
Exceptions are domain-specific, handled in middleware, consistent response format.

### 8. Testing First
Code structure designed for testability. Services can be unit tested in isolation.

---

## Rollback Plan

If anything goes wrong:

**Before Deploying:**
- Tag current version: `git tag -a pre-refactor -m "Before architecture refactor"`
- Keep feature flag disabled during testing
- Run full regression test suite

**If Issues Found During Testing:**
- Revert branch: `git checkout pre-refactor`
- Disable feature flag
- Deploy rollback
- Investigate issue
- Fix in development
- Redeploy

**Time to Rollback**: < 30 minutes with feature flag, < 2 hours with git revert

---

## Success Story Example

**Before Refactoring:**
- Adding a new endpoint takes 2-3 days (understand existing code)
- Fixing a bug requires touching multiple files and functions
- Testing requires real files, can't isolate
- Performance issues require profiling all 6,863 lines

**After Refactoring:**
- Adding a new endpoint takes 2-3 hours (clear structure)
- Fixing a bug is isolated to one service
- Testing is unit tests + integration tests (fast, no files)
- Performance issues are profiled in specific services

**ROI**: First 2 new features pay for the refactoring effort

---

## Contact & Questions

- **Architecture Lead**: [Name] (can answer design questions)
- **Implementation Lead**: [Name] (can answer coding questions)
- **QA Lead**: [Name] (can answer testing questions)

---

**Document Version**: 1.0  
**Last Updated**: 2026-05-04  
**Status**: Ready for Review  

**Next Steps**: 
1. Read `REFACTORING_EXECUTIVE_SUMMARY.md`
2. Make decision: Approve / Defer / Modify
3. Schedule kickoff meeting
4. Begin Phase 1
