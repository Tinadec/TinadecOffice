# Ponytail Rules for TinadecOffice

## Core Decision Ladder

Before writing code, follow this decision ladder:

1. **YAGNI Check**: Does this need to exist? → No: skip it
2. **Reuse Check**: Already in this codebase? → Reuse it, don't rewrite
3. **Stdlib Check**: Stdlib does it? → Use it
4. **Platform Check**: Native platform feature? → Use it
5. **Dependency Check**: Installed dependency? → Use it
6. **One-liner Check**: One line? → One line
7. **Minimum Viable**: Only then: the minimum that works

## Safety Rules

### NEVER Remove
- Validation code
- Error handling mechanisms
- Security guards
- Accessibility features
- Approval gates

### ALWAYS Maintain
- Existing safety patterns
- Security boundaries
- Error propagation
- Input validation

## Layer-Specific Rules

### Desktop Layer (Electron + Vue 3)
- Prefer Vue 3 Composition API over Options API
- Reuse existing components from `apps/desktop/src/components/`
- Use Tailwind CSS for styling, avoid custom CSS when possible
- Leverage built-in HTML5 features (e.g., `<input type="date">`)
- Avoid unnecessary component wrappers

### Gateway Layer (Elysia TypeScript)
- Keep routes thin and simple
- Do not add middleware logic unless absolutely necessary
- Proxy requests to Core, do not implement business logic
- Use Elysia's built-in validation (t.Object, t.String)
- Avoid introducing new validation libraries

### Core Layer (.NET 10 C#)
- Use minimal API patterns
- Prefer framework built-in logging (ILogger<T>)
- Use dependency injection, avoid service locators
- Leverage existing services from `src/TinadecCore/Services/`
- Avoid over-abstraction

## Common Patterns

### Date/Time Handling
```typescript
// Good: Use native date input
<input type="date" v-model="date">

// Bad: Install third-party date picker
// <DatePicker v-model="date" />
```

### HTTP Requests
```typescript
// Good: Use built-in fetch
const response = await fetch('/api/v1/endpoint')

// Bad: Install axios or other HTTP client
// const response = await axios.get('/api/v1/endpoint')
```

### Logging
```csharp
// Good: Use .NET built-in logging
_logger.LogInformation("Operation completed")

// Bad: Introduce Serilog or NLog
// Log.Information("Operation completed")
```

### Configuration
```typescript
// Good: Use environment variables
const apiUrl = process.env.API_URL

// Bad: Install config libraries
// const config = require('config')
```

## Code Examples

### Vue Component (Desktop Layer)
```vue
<template>
  <!-- Good: Simple, minimal -->
  <input type="date" v-model="selectedDate">
  
  <!-- Bad: Over-engineered -->
  <!-- <DatePicker v-model="selectedDate" :format="format" /> -->
</template>

<script setup>
import { ref } from 'vue'

const selectedDate = ref(null)
</script>
```

### Elysia Route (Gateway Layer)
```typescript
// Good: Simple validation
app.post('/api/v1/endpoint', {
  body: t.Object({
    name: t.String()
  })
}, (context) => {
  return proxyToCore(context)
})

// Bad: Complex middleware
// app.post('/api/v1/endpoint', validate, transform, authorize, handler)
```

### .NET Service (Core Layer)
```csharp
// Good: Minimal API with DI
app.MapPost("/api/v1/endpoint", async (MyService service) =>
{
    return await service.ProcessAsync();
});

// Bad: Over-abstracted controller
// [ApiController]
// [Route("api/v1")]
// public class MyController : ControllerBase { }
```

## Integration with CodeGraph

When using Ponytail principles, leverage CodeGraph for:
- Understanding existing code before writing new code
- Verifying reuse opportunities
- Checking impact of changes
- Validating architecture boundaries

## References

- [Ponytail Official Repository](https://github.com/DietrichGebert/ponytail)
- [TinadecOffice Architecture](../docs/architecture.md)
- [Architecture](../AGENTS.md)
