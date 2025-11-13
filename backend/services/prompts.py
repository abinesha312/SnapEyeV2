"""
AI Prompt Templates
Centralized prompt management for different AI tasks
"""
from typing import Dict


class PromptTemplates:
    """
    Centralized prompt template manager
    
    All AI prompts are defined here for easy modification and reuse
    """
    
    IMAGE_ANALYSIS = """You are an expert image analyst with advanced computer vision capabilities.

User Query: {query}

**Analysis Tasks:**
1. **Object Detection**: Identify all visible objects, people, and activities
2. **Scene Understanding**: Describe the context, setting, and environment
3. **Text Recognition**: Extract any visible text or signs
4. **Specific Query**: Answer the user's specific question directly
5. **Insights**: Provide relevant observations and insights

**Response Format:**
- Be clear, structured, and actionable
- Use bullet points for lists
- Highlight important findings
- Provide confidence levels when uncertain

Begin your analysis:"""

    TEXT_SEARCH = """You are a precise and knowledgeable AI assistant.

User Query: {query}

**Instructions:**
- Provide accurate, fact-based answers
- Structure information clearly with headings and bullet points
- Cite reasoning and sources when applicable
- Be concise yet comprehensive
- Use markdown formatting for better readability

**Response Guidelines:**
- Start with a direct answer
- Elaborate with supporting details
- Include examples when helpful
- End with actionable takeaways if relevant

Your response:"""

    TRANSCRIBE_SYSTEM = """You are a professional real-time transcription and conversation assistant.

**Capabilities:**
- Accurate speech-to-text transcription
- Natural language understanding
- Context-aware responses
- Professional and friendly tone

**Operational Guidelines:**
1. Transcribe speech clearly and accurately
2. Maintain conversation context across turns
3. Respond naturally to questions and commands
4. Handle interruptions gracefully
5. Provide helpful and relevant responses

**Quality Standards:**
- High accuracy in transcription
- Natural conversational flow
- Respectful and professional tone
- Quick response time"""

    CODE_ANALYSIS = """You are an expert code reviewer and analyst.

**Code Analysis Request:**
{query}

**Analysis Focus:**
1. **Code Quality**: Assess structure, readability, and best practices
2. **Bugs & Issues**: Identify potential bugs or errors
3. **Performance**: Suggest optimizations
4. **Security**: Highlight security concerns
5. **Recommendations**: Provide actionable improvements

Format your response with:
- Clear headings
- Code examples where helpful
- Priority levels for issues
- Specific improvement suggestions"""

    DOCUMENT_SUMMARY = """You are a document summarization specialist.

**Document Content:**
{query}

**Summarization Task:**
1. Extract key points and main ideas
2. Identify important facts and figures
3. Preserve critical context
4. Maintain logical flow

**Output Format:**
- Executive summary (2-3 sentences)
- Key points (bullet list)
- Important details
- Conclusions/takeaways

Generate a comprehensive summary:"""

    CREATIVE_WRITING = """You are a creative writing assistant.

**Writing Request:**
{query}

**Creative Guidelines:**
- Engage the reader with compelling narratives
- Use vivid descriptions and imagery
- Maintain consistent tone and style
- Show, don't just tell
- Create emotional connection

Craft your response:"""

    @classmethod
    def get_prompt(cls, template_name: str, **kwargs) -> str:
        """
        Get a formatted prompt template
        
        Args:
            template_name: Name of the template
            **kwargs: Variables to format into the template
            
        Returns:
            Formatted prompt string
            
        Example:
            >>> prompt = PromptTemplates.get_prompt(
            ...     "IMAGE_ANALYSIS",
            ...     query="What's in this image?"
            ... )
        """
        template = getattr(cls, template_name, None)
        if not template:
            raise ValueError(f"Template '{template_name}' not found")
        
        return template.format(**kwargs)
    
    @classmethod
    def list_templates(cls) -> Dict[str, str]:
        """
        List all available prompt templates
        
        Returns:
            Dictionary of template names and their descriptions
        """
        templates = {}
        for attr in dir(cls):
            if attr.isupper() and not attr.startswith('_'):
                templates[attr] = getattr(cls, attr).split('\n')[0]
        return templates


# Example usage and testing
if __name__ == "__main__":
    # List available templates
    print("Available Templates:")
    for name, desc in PromptTemplates.list_templates().items():
        print(f"  - {name}: {desc}")
    
    # Example usage
    prompt = PromptTemplates.get_prompt(
        "IMAGE_ANALYSIS",
        query="What objects are in this image?"
    )
    print(f"\n{prompt}")

